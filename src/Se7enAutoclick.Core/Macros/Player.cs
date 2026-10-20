using Se7enAutoclick.Core.Input;

namespace Se7enAutoclick.Core.Macros;

public enum LoopMode { Infinite, Count, Duration }

/// <summary>Réglages de lecture (mémorisés).</summary>
public sealed class PlaybackSettings
{
    public LoopMode Loop { get; set; } = LoopMode.Infinite;
    public int LoopCount { get; set; } = 10;
    public double LoopMinutes { get; set; } = 30;
    /// <summary>Vitesse (1 = identique, 2 = deux fois plus vite).</summary>
    public double Speed { get; set; } = 1;
    /// <summary>Pause entre deux boucles, en ms.</summary>
    public double PauseMs { get; set; }
    /// <summary>Variation aléatoire des délais, en % (0 = à l'identique).</summary>
    public double RandomPercent { get; set; }
    public MouseReplay Mouse { get; set; } = MouseReplay.Relative;
    /// <summary>S'arrête si une autre fenêtre passe au premier plan (alt-tab, notification…).</summary>
    public bool StopOnFocusChange { get; set; } = true;
    /// <summary>Compte à rebours avant la lecture, en secondes (0 = tout de suite).</summary>
    public double CountdownSeconds { get; set; }
}

/// <summary>
/// Rejoue une macro à l'identique, en boucle : chaque geste part à son temps exact (échéances absolues, attente
/// précise). Tout ce qui est enfoncé est relâché à l'arrêt et entre deux boucles : aucune touche ne reste bloquée.
/// </summary>
public sealed class Player
{
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private readonly HashSet<int> _keysDown = [];
    private readonly HashSet<MouseButton> _buttonsDown = [];

    public bool IsRunning => _thread is { IsAlive: true };
    /// <summary>Boucle en cours (1 = première).</summary>
    public int Loop { get; private set; }
    /// <summary>Avancement dans la boucle en cours (0..1).</summary>
    public double Progress { get; private set; }

    /// <summary>Fin de la lecture ; le texte dit pourquoi (null = terminé normalement ou arrêté).</summary>
    public event Action<string?>? Stopped;

    public void Start(Macro macro, PlaybackSettings settings)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var events = macro.Events.ToArray();
        var s = System.Text.Json.JsonSerializer.Deserialize<PlaybackSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        _thread = new Thread(() => Run(events, s, token)) { IsBackground = true, Name = "macro-player", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(1000);
        _thread = null;
    }

    private void Run(MacroEvent[] events, PlaybackSettings s, CancellationToken cancel)
    {
        string? reason = null;
        using var clock = new PreciseClock();
        try
        {
            if (events.Length == 0) { reason = "La macro est vide."; return; }
            for (int i = (int)Math.Round(s.CountdownSeconds); i > 0; i--)
                if (!clock.WaitUntil(clock.NowMs + 1000, cancel)) return;

            var target = Injector.Foreground();
            var rng = new Random();
            double speed = Math.Clamp(s.Speed, 0.05, 20);
            double length = events[^1].T;
            double begin = clock.NowMs;
            for (Loop = 1; ; Loop++)
            {
                if (s.Loop == LoopMode.Count && Loop > s.LoopCount) break;
                if (s.Loop == LoopMode.Duration && clock.NowMs - begin >= s.LoopMinutes * 60_000) break;

                double start = clock.NowMs;
                foreach (var e in events)
                {
                    double at = e.T / speed;
                    if (s.RandomPercent > 0 && e.Kind is not (EventKind.MoveTo or EventKind.MoveBy))
                        at += (rng.NextDouble() * 2 - 1) * s.RandomPercent / 100 * Math.Min(250, at);
                    if (!clock.WaitUntil(start + at, cancel)) return;
                    if (s.StopOnFocusChange && Injector.Foreground() != target)
                    {
                        reason = "Lecture arrêtée : une autre fenêtre est passée devant.";
                        return;
                    }
                    Apply(e, s.Mouse);
                    Progress = length > 0 ? e.T / length : 1;
                }
                ReleaseAll();
                if (s.PauseMs > 0 && !clock.WaitUntil(clock.NowMs + s.PauseMs, cancel)) return;
            }
        }
        finally
        {
            ReleaseAll();
            Progress = 0;
            Stopped?.Invoke(reason);
        }
    }

    private void Apply(MacroEvent e, MouseReplay mouse)
    {
        switch (e.Kind)
        {
            case EventKind.KeyDown:
                Injector.Key(e.A, true, e.B, e.Ext);
                _keysDown.Add(e.A);
                break;
            case EventKind.KeyUp:
                Injector.Key(e.A, false, e.B, e.Ext);
                _keysDown.Remove(e.A);
                break;
            case EventKind.ButtonDown:
                if (mouse == MouseReplay.Screen) Injector.MoveTo(e.B, e.C);
                Injector.Button((MouseButton)e.A, true);
                _buttonsDown.Add((MouseButton)e.A);
                break;
            case EventKind.ButtonUp:
                Injector.Button((MouseButton)e.A, false);
                _buttonsDown.Remove((MouseButton)e.A);
                break;
            case EventKind.Wheel:
            case EventKind.HWheel:
                Injector.Wheel(e.A, e.Kind == EventKind.HWheel);
                break;
            case EventKind.MoveTo:
                if (mouse == MouseReplay.Screen) Injector.MoveTo(e.A, e.B);
                break;
            case EventKind.MoveBy:
                if (mouse == MouseReplay.Relative) Injector.MoveBy(e.A, e.B);
                break;
        }
    }

    /// <summary>Relâche tout ce que la macro a enfoncé.</summary>
    private void ReleaseAll()
    {
        foreach (var vk in _keysDown) Injector.Key(vk, false);
        foreach (var b in _buttonsDown) Injector.Button(b, false);
        _keysDown.Clear();
        _buttonsDown.Clear();
    }
}
