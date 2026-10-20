using System.Diagnostics;
using Se7enAutoclick.Core;
using Se7enAutoclick.Core.Input;
using Se7enAutoclick.Core.Macros;

namespace Se7enAutoclick.Tests;

/// <summary>Couleurs, enchaînement et éditeur. Même classe de tests que l'injection : ils ne tournent pas en parallèle.</summary>
[Collection("injection")]
public class FeatureTests : IDisposable
{
    private readonly object _gate = new();
    private readonly List<(double T, Native.InputData In)> _sent = [];
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private volatile int _pixel = 0x000000;

    public FeatureTests()
    {
        Injector.Redirect = i => { lock (_gate) _sent.Add((_watch.Elapsed.TotalMilliseconds, i)); };
        ScreenColor.Redirect = (_, _) => _pixel;
    }

    public void Dispose()
    {
        Injector.Redirect = null;
        ScreenColor.Redirect = null;
    }

    private List<(double T, Native.InputData In)> Sent() { lock (_gate) return [.. _sent]; }

    private static bool IsLeftDown(Native.InputData i) => i.Type == Native.InputMouse && (i.U.Mi.Flags & Native.MouseLeftDown) != 0;

    private static Macro Sample() => new()
    {
        Name = "farm",
        Events =
        [
            new(0, EventKind.KeyDown, 0x57, 0x11),
            new(50, EventKind.MoveBy, 4, 0),
            new(100, EventKind.ButtonDown, 1),
            new(120, EventKind.ButtonUp, 1),
            new(300, EventKind.KeyUp, 0x57, 0x11),
            new(310, EventKind.MoveBy, 1, 1),
            new(500, EventKind.KeyDown, 0x20, 0x39),
            new(560, EventKind.KeyUp, 0x20, 0x39),
        ],
    };

    [Fact]
    public void Color_tolerance()
    {
        Assert.True(ScreenColor.Near(0x3DFF8C, 0x40F090, 20));
        Assert.False(ScreenColor.Near(0x3DFF8C, 0xFF0000, 20));
        Assert.False(ScreenColor.Near(-1, 0, 255)); // hors écran
    }

    [Fact]
    public void Clicker_only_clicks_while_the_color_is_there()
    {
        var clicker = new Clicker();
        clicker.Start(new ClickerSettings
        {
            IntervalMs = 10,
            Condition = new ColorCondition { Mode = ColorMode.WhenMatches, X = 5, Y = 5, Color = 0x00FF00, Tolerance = 10 },
        });
        Thread.Sleep(300);
        Assert.Equal(0, Sent().Count(s => IsLeftDown(s.In))); // la couleur n'est pas là : aucun clic
        _pixel = 0x05FA05;
        Thread.Sleep(300);
        clicker.Stop();
        Assert.InRange(Sent().Count(s => IsLeftDown(s.In)), 20, 32);
    }

    [Fact]
    public void Wait_color_step_delays_the_rest_of_the_macro()
    {
        var m = MacroEditor.InsertWaitColor(Sample(), 1, 10, 10, 0xFF0000, 16, untilGone: false); // avant le clic
        var player = new Player();
        player.Start(m, new PlaybackSettings { Loop = LoopMode.Count, LoopCount = 1, StopOnFocusChange = false });
        Thread.Sleep(400);
        Assert.True(player.Waiting);
        Assert.Equal(0, Sent().Count(s => IsLeftDown(s.In)));
        double redAt = _watch.Elapsed.TotalMilliseconds;
        _pixel = 0xFF0000;
        Thread.Sleep(700);
        Assert.False(player.IsRunning);
        double click = Sent().First(s => IsLeftDown(s.In)).T;
        Assert.InRange(click - redAt, 0, 40); // le clic part dès que la couleur apparaît
    }

    [Fact]
    public void Chain_plays_each_macro_its_number_of_times_in_order()
    {
        var a = new Macro { Name = "a", Events = [new(0, EventKind.KeyDown, 0x41), new(10, EventKind.KeyUp, 0x41)] };
        var b = new Macro { Name = "b", Events = [new(0, EventKind.KeyDown, 0x42), new(10, EventKind.KeyUp, 0x42)] };
        var player = new Player();
        player.Start([new ChainPart("a", a, 1), new ChainPart("b", b, 3)],
            new PlaybackSettings { Loop = LoopMode.Count, LoopCount = 2, StopOnFocusChange = false });
        Thread.Sleep(600);
        var order = Sent().Where(s => s.In.Type == Native.InputKeyboard && (s.In.U.Ki.Flags & Native.KeyEventKeyUp) == 0)
            .Select(s => (char)s.In.U.Ki.Vk);
        Assert.Equal("ABBBABBB", string.Concat(order));
    }

    [Fact]
    public void Editor_deletes_a_key_with_its_release()
    {
        var m = MacroEditor.DeleteStep(Sample(), 0); // ↓ Z
        Assert.DoesNotContain(m.Events, e => e.A == 0x57 && e.Kind is EventKind.KeyDown or EventKind.KeyUp);
        Assert.Equal(4, MacroEditor.StepCount(m));
    }

    [Fact]
    public void Editor_trims_start_and_keeps_held_keys()
    {
        var m = MacroEditor.TrimBefore(Sample(), 1); // commence au clic (Z est tenu à ce moment)
        Assert.Equal(0, m.Events[0].T);
        Assert.Contains(m.Events, e => e.Kind == EventKind.KeyDown && e.A == 0x57 && e.T == 0);
        Assert.Equal(0, m.Events.First(e => e.Kind == EventKind.ButtonDown).T);
        Assert.Equal(460, m.DurationMs, 3);
    }

    [Fact]
    public void Editor_trims_end()
    {
        var m = MacroEditor.TrimAfter(Sample(), 3); // garde jusqu'à ↑ Z (300 ms)
        Assert.DoesNotContain(m.Events, e => e.A == 0x20);
        Assert.Equal(350, m.DurationMs, 3);
    }

    [Fact]
    public void Editor_changes_a_delay_and_shifts_the_rest()
    {
        var m = MacroEditor.SetDelayBefore(Sample(), 4, 1000); // ↓ Espace, 200 ms après ↑ Z -> 1 000 ms
        Assert.Equal(1000, MacroEditor.DelayBefore(m, 4), 3);
        Assert.Equal(1360, m.DurationMs, 3);
        Assert.Equal(100, m.Events.First(e => e.Kind == EventKind.ButtonDown).T); // l'avant ne bouge pas
    }

    [Fact]
    public void Wait_step_survives_save_and_load()
    {
        var m = MacroEditor.InsertWaitColor(Sample(), 2, 640, 360, 0x12AB34, 30, untilGone: true);
        string path = Path.Combine(Path.GetTempPath(), $"se7en-wait-{Guid.NewGuid():N}{Macro.Extension}");
        try
        {
            m.Save(path);
            var back = Macro.Load(path);
            var w = back.Events.Single(e => e.Kind == EventKind.WaitColor);
            Assert.Equal((640, 360, 0x12AB34, true, 30), (w.A, w.B, w.C, w.Ext, w.D));
            Assert.Contains("ne soit plus", string.Join("\n", back.Steps()));
        }
        finally { File.Delete(path); }
    }
}
