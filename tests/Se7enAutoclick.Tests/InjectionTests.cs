using System.Diagnostics;
using Se7enAutoclick.Core;
using Se7enAutoclick.Core.Input;
using Se7enAutoclick.Core.Macros;

namespace Se7enAutoclick.Tests;

/// <summary>
/// L'autoclic et le lecteur, mesurés sans rien envoyer au PC : les entrées simulées sont interceptées
/// (<see cref="Injector.Redirect"/>) et horodatées.
/// </summary>
public class InjectionTests : IDisposable
{
    private readonly object _gate = new();
    private readonly List<(double T, Native.InputData In)> _sent = [];
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public InjectionTests() =>
        Injector.Redirect = i => { lock (_gate) _sent.Add((_watch.Elapsed.TotalMilliseconds, i)); };

    public void Dispose() => Injector.Redirect = null;

    private List<(double T, Native.InputData In)> Sent() { lock (_gate) return [.. _sent]; }

    private static bool IsLeftDown(Native.InputData i) => i.Type == Native.InputMouse && (i.U.Mi.Flags & Native.MouseLeftDown) != 0;

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void Clicker_hits_the_requested_speed(double cps)
    {
        var clicker = new Clicker();
        clicker.Start(new ClickerSettings { IntervalMs = 1000 / cps, Limit = ClickLimit.Duration, DurationSeconds = 1 });
        Thread.Sleep(1300);
        clicker.Stop();

        var downs = Sent().Where(s => IsLeftDown(s.In)).Select(s => s.T).ToList();
        Assert.InRange(downs.Count, cps * 0.97, cps * 1.03 + 1);
        var gaps = downs.Zip(downs.Skip(1), (a, b) => b - a).ToList();
        double expected = 1000 / cps;
        Assert.True(Math.Abs(gaps.Average() - expected) < expected * 0.03, $"intervalle moyen {gaps.Average():F3} ms au lieu de {expected:F3}");
        // chaque appui est bien relâché
        Assert.Equal(downs.Count, Sent().Count(s => (s.In.U.Mi.Flags & Native.MouseLeftUp) != 0));
    }

    [Fact]
    public void Clicker_count_limit_and_double_click()
    {
        var clicker = new Clicker();
        clicker.Start(new ClickerSettings { IntervalMs = 5, Limit = ClickLimit.Count, Count = 30, DoubleClick = true, Button = MouseButton.Right });
        Thread.Sleep(600);
        Assert.False(clicker.IsRunning);
        Assert.Equal(60, Sent().Count(s => (s.In.U.Mi.Flags & Native.MouseRightDown) != 0));
        Assert.Equal(30, clicker.Clicks);
    }

    [Fact]
    public void Clicker_can_press_a_key_with_its_scan_code()
    {
        var clicker = new Clicker();
        clicker.Start(new ClickerSettings { UseKey = true, KeyVk = 0x45, IntervalMs = 10, Limit = ClickLimit.Count, Count = 5 });
        Thread.Sleep(300);
        var keys = Sent().Where(s => s.In.Type == Native.InputKeyboard).ToList();
        Assert.Equal(10, keys.Count);
        Assert.All(keys, k => Assert.True((k.In.U.Ki.Flags & Native.KeyEventScanCode) != 0 && k.In.U.Ki.Scan != 0));
    }

    [Fact]
    public void Player_replays_in_order_with_exact_timing_and_loops()
    {
        var macro = new Macro
        {
            Events =
            [
                new(0, EventKind.KeyDown, 0x57, 0x11),     // Z enfoncé (avancer)
                new(40, EventKind.MoveBy, 25, -3),          // caméra
                new(120, EventKind.ButtonDown, 1, 0, 0),
                new(135, EventKind.ButtonUp, 1, 0, 0),
                new(200, EventKind.KeyUp, 0x57, 0x11),
                new(260, EventKind.KeyDown, 0x20, 0x39),    // Espace tenu jusqu'à la fin : relâché en fin de boucle
            ],
        };
        var player = new Player();
        string? reason = "?";
        player.Stopped += r => reason = r;
        player.Start(macro, new PlaybackSettings { Loop = LoopMode.Count, LoopCount = 3, StopOnFocusChange = false });
        Thread.Sleep(1200);
        Assert.False(player.IsRunning);
        Assert.Null(reason);

        var sent = Sent();
        var keyDowns = sent.Where(s => s.In.Type == Native.InputKeyboard && (s.In.U.Ki.Flags & Native.KeyEventKeyUp) == 0).ToList();
        Assert.Equal(6, keyDowns.Count); // Z et Espace, 3 boucles
        var moves = sent.Where(s => s.In.Type == Native.InputMouse && s.In.U.Mi.Flags == Native.MouseMove).ToList();
        Assert.Equal(3, moves.Count);
        Assert.All(moves, m => Assert.Equal((25, -3), (m.In.U.Mi.Dx, m.In.U.Mi.Dy)));

        // boucle 1 : clic gauche 120 ms après le début, à ±2 ms près
        double t0 = keyDowns[0].T;
        double click = sent.First(s => IsLeftDown(s.In)).T;
        Assert.InRange(click - t0, 118, 124);
        // Espace relâché à chaque fin de boucle (aucune touche bloquée)
        int spaceUps = sent.Count(s => s.In.Type == Native.InputKeyboard && s.In.U.Ki.Vk == 0x20 && (s.In.U.Ki.Flags & Native.KeyEventKeyUp) != 0);
        Assert.True(spaceUps >= 3, $"{spaceUps} relâchements d'Espace");
    }

    [Fact]
    public void Stopping_playback_releases_held_keys()
    {
        var macro = new Macro { Events = [new(0, EventKind.KeyDown, 0x44, 0x20), new(5000, EventKind.KeyUp, 0x44, 0x20)] };
        var player = new Player();
        player.Start(macro, new PlaybackSettings { StopOnFocusChange = false });
        Thread.Sleep(150);
        player.Stop();
        var last = Sent().Last(s => s.In.Type == Native.InputKeyboard);
        Assert.Equal(0x44, last.In.U.Ki.Vk);
        Assert.True((last.In.U.Ki.Flags & Native.KeyEventKeyUp) != 0);
    }

    [Fact]
    public void Speed_doubles_the_pace()
    {
        var macro = new Macro { Events = [new(0, EventKind.ButtonDown, 1), new(400, EventKind.ButtonUp, 1)] };
        var player = new Player();
        player.Start(macro, new PlaybackSettings { Loop = LoopMode.Count, LoopCount = 1, Speed = 2, StopOnFocusChange = false });
        Thread.Sleep(500);
        var s = Sent();
        double down = s.First(x => IsLeftDown(x.In)).T, up = s.First(x => (x.In.U.Mi.Flags & Native.MouseLeftUp) != 0).T;
        Assert.InRange(up - down, 198, 204);
    }
}
