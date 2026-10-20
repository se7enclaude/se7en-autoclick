using Se7enAutoclick.Core.Input;

namespace Se7enAutoclick.Core;

public enum ClickTrigger { Toggle, Hold }
public enum ClickLimit { Infinite, Count, Duration }
public enum ClickPosition { Cursor, Fixed, Points }

public sealed class ScreenPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public override string ToString() => $"{X}, {Y}";
}

/// <summary>Réglages de l'autoclic (tous mémorisés).</summary>
public sealed class ClickerSettings
{
    /// <summary>Clique avec un bouton de souris, ou appuie sur une touche du clavier.</summary>
    public bool UseKey { get; set; }
    public MouseButton Button { get; set; } = MouseButton.Left;
    public int KeyVk { get; set; } = 0x45; // E
    public bool DoubleClick { get; set; }

    /// <summary>Temps entre deux clics, en millisecondes (0,5 ms = 2 000 clics/s au mieux).</summary>
    public double IntervalMs { get; set; } = 100;
    /// <summary>Variation aléatoire de l'intervalle, en % (0 = métronome, 20 = plus humain).</summary>
    public double RandomPercent { get; set; }
    /// <summary>Durée d'appui de chaque clic en ms (0 = automatique).</summary>
    public double HoldMs { get; set; }

    public ClickTrigger Trigger { get; set; } = ClickTrigger.Toggle;
    public ClickLimit Limit { get; set; } = ClickLimit.Infinite;
    public int Count { get; set; } = 100;
    public double DurationSeconds { get; set; } = 60;

    public ClickPosition Position { get; set; } = ClickPosition.Cursor;
    public List<ScreenPoint> Points { get; set; } = [];

    /// <summary>Ne clique que si un point de l'écran a (ou n'a plus) une couleur.</summary>
    public ColorCondition Condition { get; set; } = new();

    public double Cps => 1000.0 / Math.Max(0.5, IntervalMs);

    /// <summary>Durée d'appui réellement utilisée.</summary>
    public double EffectiveHoldMs => HoldMs > 0 ? Math.Min(HoldMs, Math.Max(0, IntervalMs - 0.2)) : Math.Min(15, IntervalMs * 0.4);
}

/// <summary>
/// L'autoclic : un fil dédié en priorité haute, échéances absolues (aucune dérive) et attente précise.
/// </summary>
public sealed class Clicker
{
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private long _clicks;
    private readonly Queue<double> _recent = new();
    private readonly object _statsGate = new();

    public bool IsRunning => _thread is { IsAlive: true };
    public long Clicks => Interlocked.Read(ref _clicks);

    /// <summary>Clics réellement envoyés sur la dernière seconde.</summary>
    public double MeasuredCps
    {
        get
        {
            lock (_statsGate)
            {
                double now = Environment.TickCount64;
                while (_recent.Count > 0 && now - _recent.Peek() > 1000) _recent.Dequeue();
                return _recent.Count;
            }
        }
    }

    /// <summary>Fin du clic automatique (limite atteinte ou arrêt).</summary>
    public event Action? Stopped;

    public void Start(ClickerSettings s)
    {
        Stop();
        var settings = Clone(s);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Interlocked.Exchange(ref _clicks, 0);
        lock (_statsGate) _recent.Clear();
        _thread = new Thread(() => Run(settings, token)) { IsBackground = true, Name = "clicker", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(500);
        _thread = null;
    }

    private void Run(ClickerSettings s, CancellationToken cancel)
    {
        using var clock = new PreciseClock();
        var rng = new Random();
        double start = clock.NowMs, next = start;
        double hold = s.EffectiveHoldMs;
        int point = 0;
        try
        {
            long done = 0;
            for (long n = 0; ; n++)
            {
                if (s.Limit == ClickLimit.Count && done >= s.Count) break;
                if (s.Limit == ClickLimit.Duration && clock.NowMs - start >= s.DurationSeconds * 1000) break;
                if (!clock.WaitUntil(next, cancel)) break;

                // condition couleur pas remplie : on saute ce clic et on revérifie au prochain (au plus tard 50 ms)
                if (!s.Condition.Satisfied())
                {
                    next = Math.Max(next + Math.Min(s.IntervalMs, 50), clock.NowMs);
                    start = next - (n + 1) * s.IntervalMs;
                    continue;
                }

                if (s.Position == ClickPosition.Fixed && s.Points.Count > 0) Injector.MoveTo(s.Points[0].X, s.Points[0].Y);
                else if (s.Position == ClickPosition.Points && s.Points.Count > 0)
                {
                    var p = s.Points[point++ % s.Points.Count];
                    Injector.MoveTo(p.X, p.Y);
                }

                for (int k = 0; k < (s.DoubleClick ? 2 : 1); k++)
                {
                    Press(s, true);
                    if (hold > 0 && !clock.WaitUntil(clock.NowMs + hold, cancel)) { Press(s, false); return; }
                    Press(s, false);
                    // double-clic : écart court (30 ms au plus, moins à très haute vitesse)
                    if (s.DoubleClick && k == 0 && !clock.WaitUntil(clock.NowMs + Math.Max(hold, Math.Min(30, s.IntervalMs / 3)), cancel)) return;
                }
                done++;
                Interlocked.Increment(ref _clicks);
                lock (_statsGate) _recent.Enqueue(Environment.TickCount64);

                // échéance suivante depuis le départ : la variation aléatoire ne s'accumule pas
                double jitter = s.RandomPercent > 0 ? (rng.NextDouble() * 2 - 1) * s.RandomPercent / 100 * s.IntervalMs : 0;
                next = start + (n + 1) * s.IntervalMs + jitter;
                if (next < clock.NowMs - s.IntervalMs * 4) start = clock.NowMs - (n + 1) * s.IntervalMs; // PC bloqué : on repart sans rafale
            }
        }
        finally
        {
            Stopped?.Invoke();
        }
    }

    private static void Press(ClickerSettings s, bool down)
    {
        if (s.UseKey) Injector.Key(s.KeyVk, down);
        else Injector.Button(s.Button, down);
    }

    private static ClickerSettings Clone(ClickerSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<ClickerSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
}
