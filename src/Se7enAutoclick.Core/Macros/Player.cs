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

    /// <summary>Lit l'enchaînement (<see cref="Chain"/>) au lieu de la seule macro choisie.</summary>
    public bool UseChain { get; set; }
    public List<ChainEntry> Chain { get; set; } = [];
}

/// <summary>Une macro de l'enchaînement, jouée <see cref="Repeat"/> fois d'affilée.</summary>
public sealed class ChainEntry
{
    public string Macro { get; set; } = "";
    public int Repeat { get; set; } = 1;
}

/// <summary>Un morceau prêt à jouer : la macro et son nombre de répétitions.</summary>
public sealed record ChainPart(string Name, Macro Macro, int Repeat);

/// <summary>
/// Rejoue une macro à l'identique, en boucle : chaque geste part à son temps exact (échéances absolues, attente
/// précise). Tout ce qui est enfoncé est relâché à l'arrêt et entre deux boucles : aucune touche ne reste bloquée.
/// </summary>
public sealed class Player
{
    /// <summary>À la lecture, les déplacements de souris plus rapprochés que ça sont regroupés (ms).</summary>
    public const double MoveGroupMs = 4.0;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private readonly Dictionary<int, (int Scan, bool Ext)> _keysDown = [];
    private readonly HashSet<MouseButton> _buttonsDown = [];
    private readonly object _releaseGate = new();
    private volatile bool _running;
    private int _run;

    /// <summary>Faux dès que la lecture est finie (avant <see cref="Stopped"/>), même si le fil n'a pas encore disparu.</summary>
    public bool IsRunning => _running;
    /// <summary>Boucle en cours (1 = première).</summary>
    public int Loop { get; private set; }
    /// <summary>Macro en cours (utile dans un enchaînement).</summary>
    public string Current { get; private set; } = "";
    /// <summary>Attend une couleur à l'écran.</summary>
    public bool Waiting { get; private set; }
    /// <summary>Avancement dans la boucle en cours (0..1).</summary>
    public double Progress { get; private set; }

    /// <summary>Fin de la lecture ; le texte dit pourquoi (null = terminé normalement ou arrêté).</summary>
    public event Action<string?>? Stopped;

    public void Start(Macro macro, PlaybackSettings settings) => Start([new ChainPart(macro.Name, macro, 1)], settings);

    /// <summary>Joue les morceaux l'un après l'autre (chacun répété), le tout en boucle selon les réglages.</summary>
    public void Start(IReadOnlyList<ChainPart> parts, PlaybackSettings settings)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var s = System.Text.Json.JsonSerializer.Deserialize<PlaybackSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        var list = parts.Where(p => p.Repeat > 0 && p.Macro.Events.Count > 0)
            .Select(p => (p.Name, Events: Prepare(p.Macro.Events, s.Mouse), p.Repeat)).ToList();
        int run = Interlocked.Increment(ref _run);
        _running = true;
        _thread = new Thread(() => Run(list, s, token, run)) { IsBackground = true, Name = "macro-player", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(1000);
        _thread = null;
    }

    /// <summary>
    /// Prépare une macro pour la lecture : on retire les déplacements du mode de souris qui ne sert pas (une souris à
    /// 1 000 Hz en enregistre des milliers par minute) et on regroupe ceux qui se suivent à moins de
    /// <see cref="MoveGroupMs"/> (distance totale conservée). Le dernier geste est toujours gardé : il marque la fin
    /// de la boucle.
    /// </summary>
    public static MacroEvent[] Prepare(IReadOnlyList<MacroEvent> events, MouseReplay mouse)
    {
        var unused = mouse == MouseReplay.Relative ? EventKind.MoveTo : EventKind.MoveBy;
        var used = mouse == MouseReplay.Relative ? EventKind.MoveBy : EventKind.MoveTo;
        var list = new List<MacroEvent>(events.Count);
        int group = -1; // index du déplacement en cours de regroupement
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            bool last = i == events.Count - 1;
            if (e.Kind == unused && !last) continue;
            if (e.Kind == used && !last)
            {
                if (group >= 0 && e.T - list[group].T < MoveGroupMs)
                {
                    var g = list[group];
                    list[group] = used == EventKind.MoveBy ? g with { A = g.A + e.A, B = g.B + e.B } : g with { A = e.A, B = e.B };
                    continue;
                }
                list.Add(e);
                group = list.Count - 1;
                continue;
            }
            list.Add(e);
            group = -1;
        }
        return [.. list];
    }

    private void Run(List<(string Name, MacroEvent[] Events, int Repeat)> parts, PlaybackSettings s, CancellationToken cancel, int run)
    {
        string? reason = null;
        using var clock = new PreciseClock();
        try
        {
            if (parts.Count == 0) { reason = "Rien à lire : la macro est vide."; return; }
            for (int i = (int)Math.Round(s.CountdownSeconds); i > 0; i--)
                if (!clock.WaitUntil(clock.NowMs + 1000, cancel)) return;

            var target = Injector.Foreground();
            var rng = new Random();
            double speed = Math.Clamp(s.Speed, 0.05, 20);
            double begin = clock.NowMs;
            for (Loop = 1; ; Loop++)
            {
                if (s.Loop == LoopMode.Count && Loop > s.LoopCount) break;
                if (s.Loop == LoopMode.Duration && clock.NowMs - begin >= s.LoopMinutes * 60_000) break;

                foreach (var (name, events, repeat) in parts)
                {
                    Current = name;
                    for (int r = 0; r < repeat; r++)
                    {
                        if (s.Loop == LoopMode.Duration && clock.NowMs - begin >= s.LoopMinutes * 60_000) return;
                        if (!PlayOnce(events, s, speed, rng, clock, target, cancel, ref reason)) return;
                        ReleaseAll();
                    }
                }
                if (s.PauseMs > 0 && !clock.WaitUntil(clock.NowMs + s.PauseMs, cancel)) return;
            }
        }
        catch (Exception ex)
        {
            reason = "Lecture arrêtée : erreur inattendue (" + ex.Message + ").";
        }
        finally
        {
            ReleaseAll();
            Progress = 0;
            Waiting = false;
            if (Volatile.Read(ref _run) == run) _running = false;
            Stopped?.Invoke(reason);
        }
    }

    /// <summary>Un passage de la macro. Retourne false si la lecture doit s'arrêter.</summary>
    private bool PlayOnce(MacroEvent[] events, PlaybackSettings s, double speed, Random rng, PreciseClock clock, IntPtr target,
                         CancellationToken cancel, ref string? reason)
    {
        double length = events[^1].T;
        double start = clock.NowMs;
        foreach (var e in events)
        {
            double at = e.T / speed;
            if (s.RandomPercent > 0 && e.IsStep)
                at += (rng.NextDouble() * 2 - 1) * s.RandomPercent / 100 * Math.Min(250, at);
            if (!clock.WaitUntil(start + at, cancel)) return false;
            if (s.StopOnFocusChange && Injector.Foreground() != target)
            {
                reason = "Lecture arrêtée : une autre fenêtre est passée devant.";
                return false;
            }
            if (e.Kind == EventKind.WaitColor)
            {
                // on attend la couleur, puis toute la suite de la macro est décalée du temps d'attente
                double waitFrom = clock.NowMs;
                Waiting = true;
                while (ScreenColor.Near(ScreenColor.At(e.A, e.B), e.C, e.D) == e.Ext)
                {
                    if (!clock.WaitUntil(clock.NowMs + 15, cancel)) { Waiting = false; return false; }
                    if (s.StopOnFocusChange && Injector.Foreground() != target)
                    {
                        reason = "Lecture arrêtée : une autre fenêtre est passée devant.";
                        Waiting = false;
                        return false;
                    }
                }
                Waiting = false;
                start += clock.NowMs - waitFrom;
                continue;
            }
            Apply(e, s.Mouse);
            Progress = length > 0 ? e.T / length : 1;
        }
        return true;
    }

    private void Apply(MacroEvent e, MouseReplay mouse)
    {
        switch (e.Kind)
        {
            case EventKind.KeyDown:
                Injector.Key(e.A, true, e.B, e.Ext);
                lock (_releaseGate) _keysDown[e.A] = (e.B, e.Ext);
                break;
            case EventKind.KeyUp:
                Injector.Key(e.A, false, e.B, e.Ext);
                lock (_releaseGate) _keysDown.Remove(e.A);
                break;
            case EventKind.ButtonDown:
                if (mouse == MouseReplay.Screen) Injector.MoveTo(e.B, e.C);
                Injector.Button((MouseButton)e.A, true);
                lock (_releaseGate) _buttonsDown.Add((MouseButton)e.A);
                break;
            case EventKind.ButtonUp:
                Injector.Button((MouseButton)e.A, false);
                lock (_releaseGate) _buttonsDown.Remove((MouseButton)e.A);
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

    /// <summary>Relâche tout ce que la macro a enfoncé (avec le même code matériel qu'à l'appui). Peut être appelé
    /// d'un autre fil en cas de plantage.</summary>
    public void ReleaseAll()
    {
        lock (_releaseGate)
        {
            foreach (var (vk, k) in _keysDown) Injector.Key(vk, false, k.Scan, k.Ext);
            foreach (var b in _buttonsDown) Injector.Button(b, false);
            _keysDown.Clear();
            _buttonsDown.Clear();
        }
    }
}
