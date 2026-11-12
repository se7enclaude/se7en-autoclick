using System.Diagnostics;
using Se7enAutoclick.Core;
using Se7enAutoclick.Core.Input;
using Se7enAutoclick.Core.Macros;

namespace Se7enAutoclick.Tests;

/// <summary>Raccourcis avec modificateurs, double-clic, préparation des macros, réglages abîmés.</summary>
public class RobustnessTests
{
    private static Dictionary<string, Binding> Defaults() =>
        AppSettings.DefaultBindings().ToDictionary(b => b.Key, b => b.Value!);

    [Fact]
    public void Hotkeys_without_modifier_work_while_a_modifier_is_held()
    {
        var bindings = Defaults();
        // Maj (ou Ctrl) tenu, par toi ou par la macro : F8 arrête toujours tout
        Assert.Equal(Actions.Stop, InputHub.Match(bindings, false, 0x77, Mods.Shift));
        Assert.Equal(Actions.Stop, InputHub.Match(bindings, false, 0x77, Mods.Ctrl | Mods.Alt));
        Assert.Equal(Actions.Clicker, InputHub.Match(bindings, false, 0x75, Mods.None));
        Assert.Null(InputHub.Match(bindings, false, 0x41, Mods.Shift));
    }

    [Fact]
    public void Hotkeys_with_modifier_need_their_modifier_and_win_over_plain_ones()
    {
        var bindings = Defaults();
        bindings[Actions.Play] = Binding.Key(0x77, Mods.Ctrl); // Ctrl + F8 : lecture ; F8 seul : tout arrêter
        Assert.Equal(Actions.Play, InputHub.Match(bindings, false, 0x77, Mods.Ctrl));
        Assert.Equal(Actions.Stop, InputHub.Match(bindings, false, 0x77, Mods.None));
        bindings.Remove(Actions.Stop);
        Assert.Null(InputHub.Match(bindings, false, 0x77, Mods.None));
        Assert.Null(InputHub.Match(bindings, false, 0x77, Mods.Shift));
        // bouton de souris
        bindings[Actions.Record] = Binding.Mouse(MouseButton.X1);
        Assert.Equal(Actions.Record, InputHub.Match(bindings, true, (int)MouseButton.X1, Mods.Shift));
        Assert.Null(InputHub.Match(bindings, false, (int)MouseButton.X1, Mods.None));
    }

    [Fact]
    public void Physical_modifiers_follow_real_keys_only()
    {
        var mods = new PhysicalMods();
        mods.Update(0xA0, true);  // Maj gauche
        mods.Update(0xA3, true);  // Ctrl droit
        mods.Update(0x41, true);  // A : pas un modificateur
        Assert.Equal(Mods.Shift | Mods.Ctrl, mods.Current());
        mods.Update(0xA0, false);
        Assert.Equal(Mods.Ctrl, mods.Current());
        // relâchement manqué (verrouillage…) : Windows dit la touche relâchée, on l'oublie
        Assert.Equal(Mods.None, mods.Current(_ => false));
        Assert.Equal(Mods.None, mods.Current());
        mods.Update(0x5B, true);
        mods.Clear();
        Assert.Equal(Mods.None, mods.Current());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Double_click_fits_in_the_interval(double interval)
    {
        var s = new ClickerSettings { IntervalMs = interval, DoubleClick = true };
        Assert.True(2 * s.EffectiveHoldMs + s.DoubleClickGapMs <= interval * 0.75 + 1e-9,
            $"appui {s.EffectiveHoldMs} + écart {s.DoubleClickGapMs} + appui pour {interval} ms");
        var forced = new ClickerSettings { IntervalMs = interval, DoubleClick = true, HoldMs = 500 };
        Assert.True(2 * forced.EffectiveHoldMs + forced.DoubleClickGapMs <= interval * 0.75 + 1e-9);
    }

    [Fact]
    public void Double_click_keeps_the_usual_timing_at_low_speed()
    {
        var s = new ClickerSettings { IntervalMs = 1000, DoubleClick = true };
        Assert.Equal(15, s.EffectiveHoldMs);
        Assert.Equal(30, s.DoubleClickGapMs);
    }

    [Fact]
    public void Prepare_drops_unused_mouse_mode_and_groups_moves()
    {
        var events = new List<MacroEvent> { new(0, EventKind.KeyDown, 0x57, 0x11) };
        for (int i = 1; i <= 1000; i++) // 1 s : souris à 1 000 Hz, enregistrée dans les deux modes
        {
            events.Add(new(i, EventKind.MoveBy, 2, -1));
            events.Add(new(i, EventKind.MoveTo, 100 + i, 50));
        }
        events.Add(new(1000.5, EventKind.KeyUp, 0x57, 0x11));
        events.Add(new(1200, EventKind.MoveTo, 0, 0)); // fin de boucle : gardée même si inutile

        var relative = Player.Prepare(events, MouseReplay.Relative);
        var moves = relative.Where(e => e.Kind == EventKind.MoveBy).ToList();
        Assert.DoesNotContain(relative[..^1], e => e.Kind == EventKind.MoveTo);
        Assert.True(moves.Count <= 1000 / Player.MoveGroupMs + 1, $"{moves.Count} déplacements");
        Assert.Equal(2000, moves.Sum(e => e.A));
        Assert.Equal(-1000, moves.Sum(e => e.B));
        Assert.Equal(1200, relative[^1].T);
        Assert.Equal(events.Count(e => e.IsStep), relative.Count(e => e.IsStep));

        var screen = Player.Prepare(events, MouseReplay.Screen);
        var positions = screen.Where(e => e.Kind == EventKind.MoveTo).ToList();
        Assert.DoesNotContain(screen, e => e.Kind == EventKind.MoveBy);
        Assert.True(positions.Count <= 1000 / Player.MoveGroupMs + 2);
        Assert.Equal(1100, positions[^2].A); // dernière position avant la fin de boucle
    }

    [Fact]
    public void Prepare_never_merges_moves_across_a_click()
    {
        var events = new List<MacroEvent>
        {
            new(0, EventKind.MoveBy, 5, 0),
            new(1, EventKind.ButtonDown, 1),
            new(2, EventKind.MoveBy, 7, 0),
            new(3, EventKind.ButtonUp, 1),
        };
        var p = Player.Prepare(events, MouseReplay.Relative);
        Assert.Equal([EventKind.MoveBy, EventKind.ButtonDown, EventKind.MoveBy, EventKind.ButtonUp], p.Select(e => e.Kind));
    }

    [Fact]
    public void Recorder_releases_held_keys_with_their_scan_code()
    {
        var r = new Recorder(0);
        r.Key(10, 0x26, 0x48, true, true); // ↑ (touche étendue) encore tenue à l'arrêt
        var m = r.Finish("t", 100);
        var up = m.Events.Single(e => e.Kind == EventKind.KeyUp);
        Assert.Equal((0x26, 0x48, true), (up.A, up.B, up.Ext));
    }

    [Fact]
    public void Corrupted_settings_are_set_aside()
    {
        string dir = Path.Combine(Path.GetTempPath(), "se7en-autoclick-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"Clicker\": { \"IntervalMs\": 12,"); // coupé en plein milieu
            var s = AppSettings.Load(path);
            Assert.Equal(100, s.Clicker.IntervalMs);
            Assert.NotNull(AppSettings.LoadProblem);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".corrompu"));

            s.Clicker.IntervalMs = 25;
            s.Save(path);
            Assert.Equal(25, AppSettings.Load(path).Clicker.IntervalMs);
            Assert.Null(AppSettings.LoadProblem);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Mesures sans rien envoyer au PC (<see cref="Injector.Redirect"/>).</summary>
[Collection("injection")]
public class RobustnessInjectionTests : IDisposable
{
    private readonly object _gate = new();
    private readonly List<(double T, Native.InputData In)> _sent = [];
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public RobustnessInjectionTests() =>
        Injector.Redirect = i => { lock (_gate) _sent.Add((_watch.Elapsed.TotalMilliseconds, i)); };

    public void Dispose() => Injector.Redirect = null;

    private List<(double T, Native.InputData In)> Sent() { lock (_gate) return [.. _sent]; }

    [Fact]
    public void Double_click_keeps_the_requested_pace()
    {
        var clicker = new Clicker();
        clicker.Start(new ClickerSettings { IntervalMs = 10, DoubleClick = true, Limit = ClickLimit.Count, Count = 60 });
        Thread.Sleep(1000);
        Assert.False(clicker.IsRunning);
        var downs = Sent().Where(s => (s.In.U.Mi.Flags & Native.MouseLeftDown) != 0).Select(s => s.T).ToList();
        Assert.Equal(120, downs.Count);
        var firsts = downs.Where((_, i) => i % 2 == 0).ToList();
        double cycle = (firsts[^1] - firsts[0]) / (firsts.Count - 1);
        Assert.InRange(cycle, 9.7, 10.3); // 12 ms avant la correction
    }

    [Fact]
    public void Not_running_anymore_when_stopped_is_raised()
    {
        var clicker = new Clicker();
        bool? clickerRunning = null;
        using var clickerDone = new ManualResetEventSlim();
        clicker.Stopped += () => { clickerRunning = clicker.IsRunning; clickerDone.Set(); };
        clicker.Start(new ClickerSettings { IntervalMs = 5, Limit = ClickLimit.Count, Count = 3 });
        Assert.True(clickerDone.Wait(2000));
        Assert.False(clickerRunning);

        var player = new Player();
        bool? playerRunning = null;
        using var playerDone = new ManualResetEventSlim();
        player.Stopped += _ => { playerRunning = player.IsRunning; playerDone.Set(); };
        player.Start(new Macro { Events = [new(0, EventKind.ButtonDown, 1), new(20, EventKind.ButtonUp, 1)] },
            new PlaybackSettings { Loop = LoopMode.Count, LoopCount = 1, StopOnFocusChange = false });
        Assert.True(playerDone.Wait(2000));
        Assert.False(playerRunning);
    }
}
