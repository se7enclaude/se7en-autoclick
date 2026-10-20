using Se7enAutoclick.Core;
using Se7enAutoclick.Core.Input;
using Se7enAutoclick.Core.Macros;

namespace Se7enAutoclick.Tests;

public class EngineTests
{
    [Fact]
    public void Clock_waits_precisely()
    {
        using var clock = new PreciseClock();
        var errors = new List<double>();
        double start = clock.NowMs;
        for (int i = 1; i <= 200; i++)
        {
            double target = start + i * 2.5; // 400 évènements/s
            Assert.True(clock.WaitUntil(target, CancellationToken.None));
            errors.Add(clock.NowMs - target);
        }
        Assert.True(errors.Average() < 0.3, $"retard moyen {errors.Average():F3} ms");
        Assert.True(errors.Max() < 3, $"pire retard {errors.Max():F3} ms");
    }

    [Fact]
    public void Clock_wait_can_be_cancelled()
    {
        using var clock = new PreciseClock();
        using var cts = new CancellationTokenSource(30);
        Assert.False(clock.WaitUntil(clock.NowMs + 5000, cts.Token));
    }

    [Fact]
    public void Hold_time_never_exceeds_interval()
    {
        Assert.True(new ClickerSettings { IntervalMs = 1 }.EffectiveHoldMs < 1);
        Assert.Equal(15, new ClickerSettings { IntervalMs = 100 }.EffectiveHoldMs);
        Assert.Equal(40, new ClickerSettings { IntervalMs = 100, HoldMs = 40 }.EffectiveHoldMs);
        Assert.True(new ClickerSettings { IntervalMs = 10, HoldMs = 40 }.EffectiveHoldMs < 10);
    }

    [Fact]
    public void Recorder_keeps_first_press_only_and_releases_held_keys()
    {
        var r = new Recorder(1000);
        r.Key(1010, 0x5A, 0x11, false, true);   // Z appuyé
        r.Key(1300, 0x5A, 0x11, false, true);   // répétition automatique : ignorée
        r.Key(1500, 0x5A, 0x11, false, false);  // Z relâché
        r.Key(1600, 0x51, 0x1E, false, false);  // relâchement d'une touche appuyée avant l'enregistrement : ignoré
        r.Key(1700, 0x20, 0x39, false, true);   // Espace encore tenu à l'arrêt
        var m = r.Finish("test", 2000);

        var keys = m.Events.Where(e => e.Kind is EventKind.KeyDown or EventKind.KeyUp).ToList();
        Assert.Equal([EventKind.KeyDown, EventKind.KeyUp, EventKind.KeyDown, EventKind.KeyUp], keys.Select(e => e.Kind));
        Assert.Equal(10, keys[0].T, 3);
        Assert.Equal(1000, m.DurationMs, 3); // la pause finale fait partie de la boucle
        Assert.Equal(0x20, keys[^1].A);
    }

    [Fact]
    public void Recorder_merges_fast_mouse_moves_without_losing_distance()
    {
        var r = new Recorder(0);
        int sx = 0, sy = 0;
        for (int i = 0; i < 1000; i++) // 1 s d'une souris à 1 000 Hz
        {
            r.MoveBy(i, 3, -1);
            sx += 3; sy -= 1;
        }
        var m = r.Finish("souris", 1000);
        var moves = m.Events.Where(e => e.Kind == EventKind.MoveBy).ToList();
        Assert.True(moves.Count <= 400, $"{moves.Count} évènements gardés");
        Assert.Equal(sx, moves.Sum(e => e.A));
        Assert.Equal(sy, moves.Sum(e => e.B));
    }

    [Fact]
    public void Macro_file_round_trip()
    {
        var m = new Macro
        {
            Name = "Farm",
            Events =
            [
                new(0, EventKind.KeyDown, 0x57, 0x11),
                new(12.345, EventKind.MoveBy, -5, 7),
                new(250, EventKind.ButtonDown, 1, 640, 360),
                new(260.5, EventKind.ButtonUp, 1, 640, 360),
                new(300, EventKind.KeyUp, 0x26, 0x48, 0, true),
            ],
        };
        string path = Path.Combine(Path.GetTempPath(), $"se7en-test-{Guid.NewGuid():N}{Macro.Extension}");
        try
        {
            m.Save(path);
            var back = Macro.Load(path);
            Assert.Equal("Farm", back.Name);
            Assert.Equal(m.Events, back.Events);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Library_names_are_safe_and_unique()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"se7en-lib-{Guid.NewGuid():N}");
        try
        {
            var lib = new MacroLibrary(dir);
            Assert.Equal("Macro 1", lib.NextName());
            lib.Save(new Macro { Name = "Macro 1" });
            Assert.Equal("Macro 2", lib.NextName());
            Assert.Equal("a-b-c", MacroLibrary.SafeName("a/b:c"));
            lib.Rename("Macro 1", "Farm XP");
            Assert.Equal(["Farm XP"], lib.Names());
            Assert.Equal("Farm XP copie 1", lib.Duplicate("Farm XP"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Bindings_read_well()
    {
        Assert.Equal("F6", Binding.Key(0x75).Display);
        Assert.Equal("Ctrl + Maj + E", Binding.Key(0x45, Mods.Ctrl | Mods.Shift).Display);
        Assert.Equal("Souris bouton 4", Binding.Mouse(MouseButton.X1).Display);
        Assert.Equal(Binding.Key(0x75), Binding.Key(0x75)); // comparaison par valeur
    }

    [Fact]
    public void Settings_keep_new_default_bindings()
    {
        var s = new AppSettings();
        Assert.All(Actions.All, id => Assert.NotNull(s.Bindings[id]));
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(s.Bindings[Actions.Clicker], back.Bindings[Actions.Clicker]);
    }
}
