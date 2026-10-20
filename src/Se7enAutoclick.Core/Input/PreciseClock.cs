using System.Diagnostics;

namespace Se7enAutoclick.Core.Input;

/// <summary>
/// Attente précise à la microseconde près : Windows ne sait dormir que par pas de ~1 à 15 ms, on dort donc
/// jusqu'à 1,5 ms de l'échéance puis on finit en boucle active sur le Stopwatch. Les échéances sont absolues
/// (début + n × intervalle) : aucune dérive, même sur des heures.
/// </summary>
public sealed class PreciseClock : IDisposable
{
    private static int _users;
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public PreciseClock()
    {
        if (Interlocked.Increment(ref _users) == 1) Native.timeBeginPeriod(1);
    }

    public double NowMs => _watch.Elapsed.TotalMilliseconds;

    /// <summary>Attend jusqu'à <paramref name="targetMs"/> (temps de cette horloge). Retourne false si annulé.</summary>
    public bool WaitUntil(double targetMs, CancellationToken cancel)
    {
        while (true)
        {
            if (cancel.IsCancellationRequested) return false;
            double left = targetMs - NowMs;
            if (left <= 0) return true;
            if (left > 2.0) Thread.Sleep(Math.Min(50, (int)(left - 1.5)));
            else if (left > 0.2) Thread.Yield();
            else Thread.SpinWait(20);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _users) == 0) Native.timeEndPeriod(1);
    }
}
