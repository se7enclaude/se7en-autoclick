using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Se7enAutoclick.Core.Input;

/// <summary>
/// Attente précise à la microseconde près sans faire tourner le processeur pour rien : on dort sur un minuteur
/// haute résolution de Windows (précis au dixième de milliseconde) jusqu'à 0,3 ms de l'échéance, puis on finit en
/// boucle active sur le Stopwatch. Sans ce minuteur (Windows trop ancien), on dort par pas de 1 ms jusqu'à 1,5 ms de
/// l'échéance comme avant. Les échéances sont absolues (début + n × intervalle) : aucune dérive, même sur des heures.
/// Une horloge sert à un seul fil.
/// </summary>
public sealed class PreciseClock : IDisposable
{
    /// <summary>Fin de l'attente en boucle active (ms).</summary>
    public const double SpinMs = 0.3;

    private static int _users;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private IntPtr _timer;

    public PreciseClock()
    {
        if (Interlocked.Increment(ref _users) == 1) Native.timeBeginPeriod(1);
        try
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        }
        catch (Exception) { _timer = IntPtr.Zero; } // Windows sans minuteur haute résolution : repli
    }

    public double NowMs => _watch.Elapsed.TotalMilliseconds;

    /// <summary>Vrai si le minuteur haute résolution est disponible.</summary>
    public bool HighResolution => _timer != IntPtr.Zero;

    /// <summary>Attend jusqu'à <paramref name="targetMs"/> (temps de cette horloge). Retourne false si annulé.</summary>
    public bool WaitUntil(double targetMs, CancellationToken cancel)
    {
        while (true)
        {
            if (cancel.IsCancellationRequested) return false;
            double left = targetMs - NowMs;
            if (left <= 0) return true;
            if (_timer != IntPtr.Zero)
            {
                if (left > SpinMs) TimerSleep(Math.Min(50, left - SpinMs));
                else Thread.SpinWait(20);
            }
            else if (left > 2.0) Thread.Sleep(Math.Min(50, (int)(left - 1.5)));
            else if (left > 0.2) Thread.Yield();
            else Thread.SpinWait(20);
        }
    }

    private void TimerSleep(double ms)
    {
        long due = -(long)(ms * 10_000); // négatif = relatif, en unités de 100 ns
        if (!SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)
            || WaitForSingleObject(_timer, 1000) != 0)
        {
            // minuteur refusé : on n'y revient plus, repli sur l'ancienne attente
            CloseHandle(_timer);
            _timer = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) { CloseHandle(_timer); _timer = IntPtr.Zero; }
        if (Interlocked.Decrement(ref _users) == 0) Native.timeEndPeriod(1);
    }

    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg,
                                                  IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
