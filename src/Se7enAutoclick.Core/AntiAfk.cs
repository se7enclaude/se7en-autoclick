using System.Diagnostics;
using System.Runtime.InteropServices;
using Se7enAutoclick.Core.Input;

namespace Se7enAutoclick.Core;

public enum AfkAction { Jump, Step, Camera, Click }

public sealed class AntiAfkSettings
{
    public bool Enabled { get; set; }
    /// <summary>Un geste toutes les N minutes (Roblox éjecte après 20 minutes sans rien faire).</summary>
    public double Minutes { get; set; } = 15;
    public AfkAction Action { get; set; } = AfkAction.Jump;
}

/// <summary>
/// Anti-AFK pour Roblox : toutes les N minutes, un petit geste (saut, pas, caméra ou clic) dans chaque fenêtre
/// Roblox ouverte. Si Roblox est en arrière-plan, il passe devant le temps du geste (un quart de seconde) puis la
/// fenêtre d'avant revient — seulement quand tu ne tapes pas, pour ne jamais te couper.
/// </summary>
public sealed class AntiAfk : IDisposable
{
    private static readonly string[] RobloxProcesses = ["RobloxPlayerBeta", "Windows10Universal"];

    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private volatile AntiAfkSettings _settings = new();
    private DateTime _last = DateTime.Now;

    /// <summary>Vrai quand l'autoclic ou une macro tourne déjà (le jeu n'est pas inactif : rien à faire).</summary>
    public Func<bool>? IsBusy { get; set; }

    /// <summary>Compte rendu d'un geste envoyé (ou de l'absence de Roblox).</summary>
    public event Action<string>? Done;

    public DateTime NextAt => _last.AddMinutes(_settings.Minutes);

    public AntiAfk()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "anti-afk" };
        _thread.Start();
    }

    public void Apply(AntiAfkSettings s)
    {
        bool wasOff = !_settings.Enabled;
        _settings = new AntiAfkSettings { Enabled = s.Enabled, Minutes = Math.Clamp(s.Minutes, 0.5, 19), Action = s.Action };
        if (wasOff && s.Enabled) _last = DateTime.Now;
    }

    /// <summary>Fait le geste tout de suite (bouton « Tester »).</summary>
    public void Now() => ThreadPool.QueueUserWorkItem(_ => Nudge());

    private void Run()
    {
        while (!_cts.Token.WaitHandle.WaitOne(5000))
        {
            var s = _settings;
            if (!s.Enabled) continue;
            if (IsBusy?.Invoke() == true) { _last = DateTime.Now; continue; }
            if (DateTime.Now < NextAt) continue;
            if (IdleMs() < 1500) continue; // tu tapes ou bouges la souris : on réessaie dans 5 s
            Nudge();
        }
    }

    private void Nudge()
    {
        _last = DateTime.Now;
        var windows = RobloxWindows();
        if (windows.Count == 0)
        {
            Done?.Invoke("Anti-AFK : Roblox n'est pas ouvert.");
            return;
        }
        var action = _settings.Action;
        IntPtr previous = GetForegroundWindow();
        int sent = 0;
        foreach (var w in windows)
        {
            bool wasMinimized = IsIconic(w);
            if (w != GetForegroundWindow() && !Bring(w)) continue;
            Thread.Sleep(150);
            Gesture(action);
            sent++;
            Thread.Sleep(150);
            if (wasMinimized) ShowWindow(w, 6); // SW_MINIMIZE
        }
        if (previous != IntPtr.Zero && GetForegroundWindow() != previous) Bring(previous);
        Done?.Invoke(sent > 0
            ? $"Anti-AFK : {Label(action)} envoyé à Roblox ({DateTime.Now:HH:mm})"
            : "Anti-AFK : Windows a refusé de passer Roblox devant, réessai au prochain tour.");
    }

    public static string Label(AfkAction a) => a switch
    {
        AfkAction.Jump => "saut",
        AfkAction.Step => "petit pas",
        AfkAction.Camera => "mouvement de caméra",
        _ => "clic",
    };

    private static void Gesture(AfkAction a)
    {
        switch (a)
        {
            case AfkAction.Jump:
                Tap(0x20, 80); // Espace
                break;
            case AfkAction.Step:
                Tap(0x57, 150); // Z/W (même touche physique : on envoie le code matériel)
                Thread.Sleep(80);
                Tap(0x53, 150); // S : on revient à sa place
                break;
            case AfkAction.Camera:
                for (int i = 0; i < 10; i++) { Injector.MoveBy(6, 0); Thread.Sleep(10); }
                for (int i = 0; i < 10; i++) { Injector.MoveBy(-6, 0); Thread.Sleep(10); }
                break;
            case AfkAction.Click:
                Injector.Button(MouseButton.Left, true);
                Thread.Sleep(40);
                Injector.Button(MouseButton.Left, false);
                break;
        }
    }

    /// <summary>Appui sur une touche par sa position physique (W en QWERTY = Z en AZERTY : on avance quel que soit le clavier).</summary>
    private static void Tap(int qwertyVk, int ms)
    {
        int scan = qwertyVk switch { 0x20 => 0x39, 0x57 => 0x11, 0x53 => 0x1F, _ => 0 };
        Injector.Key(qwertyVk, true, scan);
        Thread.Sleep(ms);
        Injector.Key(qwertyVk, false, scan);
    }

    private static List<IntPtr> RobloxWindows()
    {
        var list = new List<IntPtr>();
        foreach (var name in RobloxProcesses)
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && (name != "Windows10Universal" || p.MainWindowTitle.Contains("Roblox")))
                        list.Add(p.MainWindowHandle);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        return list;
    }

    /// <summary>Met une fenêtre au premier plan (Windows ne l'autorise qu'au fil qui a le clavier : on s'y attache).</summary>
    private static bool Bring(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
        bool attached = fg != me && AttachThreadInput(me, fg, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fg, false);
        Thread.Sleep(50);
        return GetForegroundWindow() == hwnd;
    }

    private static uint IdleMs()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? (uint)Environment.TickCount - info.Time : uint.MaxValue;
    }

    public void Dispose() => _cts.Cancel();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size, Time; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
