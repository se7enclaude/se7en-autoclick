using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Se7enAutoclick.Core.Input.Native;

namespace Se7enAutoclick.Core.Input;

/// <summary>Ce que voit l'enregistreur (temps en ms depuis le début de l'horloge du hub).</summary>
public interface IInputSink
{
    void Key(double t, int vk, int scan, bool extended, bool down);
    void Button(double t, MouseButton button, bool down, int x, int y);
    void Wheel(double t, int delta, bool horizontal, int x, int y);
    void MoveTo(double t, int x, int y);
    void MoveBy(double t, int dx, int dy);
}

/// <summary>
/// Écoute tout le clavier et toute la souris de Windows, même en jeu, sur un fil dédié (hooks bas niveau +
/// Raw Input) :
/// <list type="bullet">
/// <item>raccourcis de l'appli (touche ou bouton de souris, avec modificateurs) : avalés pour que le jeu ne les voie pas ;</item>
/// <item>capture d'un nouveau raccourci (« appuie sur une touche… ») ;</item>
/// <item>Échap maintenu une seconde : arrêt d'urgence ;</item>
/// <item>enregistrement des macros : touches, clics, molette, positions, et déplacements bruts de la souris
/// (la caméra des jeux 3D, quand le curseur est bloqué).</item>
/// </list>
/// Nos propres entrées simulées (et celles des autres logiciels) sont ignorées, y compris pour les modificateurs :
/// une macro qui tient Maj ne bloque pas les raccourcis. Le hook souris n'est posé que s'il sert (enregistrement,
/// capture, choix d'un point, raccourci sur un bouton de souris).
/// </summary>
public sealed class InputHub : IDisposable
{
    private const uint WmStartRaw = WmApp + 1, WmStopRaw = WmApp + 2, WmUpdateHooks = WmApp + 3, WmReinstall = WmApp + 4,
                       WmSessionLock = WmApp + 5;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private uint _threadId;
    private IntPtr _kbHook, _msHook, _rawWindow, _module;
    private HookProc? _kbProc, _msProc;
    private readonly PhysicalMods _mods = new(); // fil des hooks seulement
    private WndProc? _wndProc;

    private readonly object _gate = new();
    private Dictionary<string, Binding> _bindings = [];
    private readonly HashSet<string> _held = [];
    private readonly HashSet<int> _swallowedKeys = [];
    private readonly HashSet<MouseButton> _swallowedButtons = [];
    private TaskCompletionSource<Binding?>? _capture;
    private TaskCompletionSource<(int X, int Y)?>? _point;
    private bool _swallowPointUp;
    private volatile IInputSink? _sink;
    private readonly BlockingCollection<Action> _events = new();
    private double _escDownAt = -1;
    private bool _escFired;

    /// <summary>Raccourci appuyé (true) ou relâché (false). Appelé hors du fil des hooks.</summary>
    public event Action<string, bool>? Hotkey;

    /// <summary>Échap maintenu une seconde.</summary>
    public event Action? EmergencyStop;

    /// <summary>Problème à signaler (hook refusé par Windows…). Appelé hors du fil des hooks.</summary>
    public event Action<string>? Problem;

    public InputHub()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "input-hub", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
        // raccourcis livrés dans l'ordre (appui puis relâchement), hors du fil des hooks
        new Thread(() => { foreach (var a in _events.GetConsumingEnumerable()) { try { a(); } catch (Exception) { } } })
            { IsBackground = true, Name = "input-events" }.Start();
    }

    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Remplace les raccourcis surveillés (identifiant → raccourci).</summary>
    public void SetBindings(IReadOnlyDictionary<string, Binding?> bindings)
    {
        lock (_gate)
            _bindings = bindings.Where(b => b.Value is not null).ToDictionary(b => b.Key, b => b.Value!);
        RequestHookUpdate();
    }

    /// <summary>Réinstalle les hooks (sortie de veille, déverrouillage : Windows a pu les retirer en silence).</summary>
    public void Reinstall() => Post(WmReinstall);

    /// <summary>Session verrouillée : les relâchements ne nous parviendront pas, on oublie tout ce qui est tenu.</summary>
    public void SessionLocked() => Post(WmSessionLock);

    private void RequestHookUpdate() => Post(WmUpdateHooks);

    private void Post(uint msg)
    {
        if (_threadId != 0) PostThreadMessage(_threadId, msg, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Attend la prochaine touche ou le prochain bouton (hors clic gauche) ; Échap annule (null).</summary>
    public Task<Binding?> CaptureAsync()
    {
        var tcs = new TaskCompletionSource<Binding?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _capture?.TrySetResult(null);
            _capture = tcs;
        }
        RequestHookUpdate();
        return tcs.Task;
    }

    public void CancelCapture()
    {
        lock (_gate)
        {
            _capture?.TrySetResult(null);
            _capture = null;
            _point?.TrySetResult(null);
            _point = null;
        }
        RequestHookUpdate();
    }

    /// <summary>Attend le prochain clic gauche n'importe où à l'écran et donne sa position (le clic n'est pas transmis).
    /// Échap annule (null).</summary>
    public Task<(int X, int Y)?> CapturePointAsync()
    {
        var tcs = new TaskCompletionSource<(int X, int Y)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _point?.TrySetResult(null);
            _point = tcs;
        }
        RequestHookUpdate();
        return tcs.Task;
    }

    /// <summary>Commence à envoyer les entrées à <paramref name="sink"/> (enregistrement).</summary>
    public void StartRecording(IInputSink sink)
    {
        _sink = sink;
        Post(WmStartRaw);
        RequestHookUpdate();
    }

    public void StopRecording()
    {
        _sink = null;
        Post(WmStopRaw);
        RequestHookUpdate();
    }

    // ------------------------------------------------------------------ fil des hooks
    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _module = GetModuleHandle(null);
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
        InstallKeyboard();
        _rawWindow = CreateRawWindow(_module);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Hwnd == IntPtr.Zero && msg.Message == WmStartRaw) SetRaw(true);
            else if (msg.Hwnd == IntPtr.Zero && msg.Message == WmStopRaw) SetRaw(false);
            else if (msg.Hwnd == IntPtr.Zero && msg.Message == WmUpdateHooks) UpdateMouseHook();
            else if (msg.Hwnd == IntPtr.Zero && msg.Message == WmReinstall) ReinstallHooks();
            else if (msg.Hwnd == IntPtr.Zero && msg.Message == WmSessionLock) ForgetHeld();
            else
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        SetRaw(false);
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        if (_rawWindow != IntPtr.Zero) DestroyWindow(_rawWindow);
    }

    private void Report(string message) => _events.Add(() => Problem?.Invoke(message));

    private void InstallKeyboard()
    {
        _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc!, _module, 0);
        if (_kbHook == IntPtr.Zero)
            Report($"Windows a refusé l'écoute du clavier (erreur {Marshal.GetLastWin32Error()}) : les raccourcis ne marchent pas. Relance l'appli.");
    }

    /// <summary>Pose ou retire le hook souris selon le besoin (fil des hooks).</summary>
    private void UpdateMouseHook()
    {
        bool need;
        lock (_gate)
            need = _sink is not null || _capture is not null || _point is not null || _swallowPointUp
                   || _swallowedButtons.Count > 0 || _bindings.Values.Any(b => b.IsMouse);
        if (need && _msHook == IntPtr.Zero)
        {
            _msHook = SetWindowsHookEx(WhMouseLl, _msProc!, _module, 0);
            if (_msHook == IntPtr.Zero)
                Report($"Windows a refusé l'écoute de la souris (erreur {Marshal.GetLastWin32Error()}) : clics et raccourcis souris non vus.");
        }
        else if (!need && _msHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_msHook);
            _msHook = IntPtr.Zero;
        }
    }

    private void ReinstallHooks()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        _kbHook = _msHook = IntPtr.Zero;
        _mods.Clear();
        InstallKeyboard();
        UpdateMouseHook();
    }

    /// <summary>Oublie les touches et raccourcis tenus ; les raccourcis tenus sont signalés relâchés.</summary>
    private void ForgetHeld()
    {
        lock (_gate)
        {
            foreach (var id in _held)
            {
                string fired = id;
                _events.Add(() => Hotkey?.Invoke(fired, false));
            }
            _held.Clear();
            _swallowedKeys.Clear();
            _swallowedButtons.Clear();
            _swallowPointUp = false;
        }
        _mods.Clear();
        _escDownAt = -1;
        _escFired = false;
        UpdateMouseHook();
    }

    private IntPtr CreateRawWindow(IntPtr module)
    {
        _wndProc = WindowProc;
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(), Proc = _wndProc, Instance = module,
            ClassName = "se7enAutoclick.RawInput." + Environment.ProcessId,
        };
        RegisterClassEx(ref wc);
        return CreateWindowEx(0, wc.ClassName, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, module, IntPtr.Zero);
    }

    private bool _rawOn;

    private void SetRaw(bool on)
    {
        if (on == _rawOn || _rawWindow == IntPtr.Zero) return;
        var dev = new[] { new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = on ? RidevInputSink : RidevRemove, Target = on ? _rawWindow : IntPtr.Zero } };
        _rawOn = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RawInputDevice>()) ? on : _rawOn;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInput && _sink is { } sink)
        {
            uint size = (uint)Marshal.SizeOf<RawInputMouse>();
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RidInput, buf, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) != uint.MaxValue)
                {
                    var raw = Marshal.PtrToStructure<RawInputMouse>(buf);
                    // hDevice nul = entrée simulée ; on ne garde que les déplacements relatifs d'une vraie souris
                    if (raw.Header.Type == RimTypeMouse && raw.Header.Device != IntPtr.Zero
                        && (raw.Mouse.Flags & MouseMoveAbsoluteFlag) == 0 && (raw.Mouse.LastX != 0 || raw.Mouse.LastY != 0))
                        sink.MoveBy(NowMs, raw.Mouse.LastX, raw.Mouse.LastY);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Modificateurs tenus physiquement (pas ceux d'une macro ni d'un autre logiciel).</summary>
    private Mods CurrentMods() => _mods.Current(Injector.IsDown);

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KbdLlHook>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WmKeyDown or WmSysKeyDown;
            bool injected = (k.Flags & LlkhfInjected) != 0;
            if (!injected) _mods.Update((int)k.Vk, down);
            if (!injected && HandleKey((int)k.Vk, (int)k.Scan, (k.Flags & LlkhfExtended) != 0, down))
                return 1; // avalé : le jeu ne le voit pas
        }
        return CallNextHookEx(_kbHook, code, wParam, lParam);
    }

    private bool HandleKey(int vk, int scan, bool extended, bool down)
    {
        double now = NowMs;
        if (vk == 0x1B)
        {
            if (!down) { _escDownAt = -1; _escFired = false; }
            else if (_escDownAt < 0) _escDownAt = now;
            else if (!_escFired && now - _escDownAt >= 1000)
            {
                _escFired = true;
                _events.Add(() => EmergencyStop?.Invoke());
            }
        }

        lock (_gate)
        {
            if (_point is not null && down && vk == 0x1B)
            {
                _point.TrySetResult(null);
                _point = null;
                _swallowedKeys.Add(vk);
                RequestHookUpdate();
                return true;
            }
            if (_capture is not null && down && !KeyNames.IsModifier(vk))
            {
                var tcs = _capture;
                _capture = null;
                _swallowedKeys.Add(vk);
                tcs.TrySetResult(vk == 0x1B ? null : Binding.Key(vk, CurrentMods()));
                RequestHookUpdate();
                return true;
            }
            if (!down && _swallowedKeys.Remove(vk))
            {
                Fire(false, vk, null, false);
                return true;
            }
            if (down && !KeyNames.IsModifier(vk) && Match(false, vk) is { } id)
            {
                if (_swallowedKeys.Add(vk)) Fire(false, vk, id, true); // première frappe seulement (pas la répétition)
                return true;
            }
        }
        _sink?.Key(now, vk, scan, extended, down);
        return false;
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MsLlHook>(lParam);
            int msg = wParam.ToInt32();
            if ((m.Flags & LlmhfInjected) == 0 && HandleMouse(msg, m))
            {
                RequestHookUpdate(); // capture finie : le hook souris sert-il encore ?
                return 1;
            }
        }
        return CallNextHookEx(_msHook, code, wParam, lParam);
    }

    private bool HandleMouse(int msg, MsLlHook m)
    {
        double now = NowMs;
        var sink = _sink;
        if (msg == WmMouseMove)
        {
            sink?.MoveTo(now, m.Pt.X, m.Pt.Y);
            return false;
        }
        if (msg is WmMouseWheel or WmMouseHWheel)
        {
            sink?.Wheel(now, (short)(m.MouseData >> 16), msg == WmMouseHWheel, m.Pt.X, m.Pt.Y);
            return false;
        }
        (MouseButton b, bool down) = msg switch
        {
            WmLButtonDown => (MouseButton.Left, true),
            WmLButtonUp => (MouseButton.Left, false),
            WmRButtonDown => (MouseButton.Right, true),
            WmRButtonUp => (MouseButton.Right, false),
            WmMButtonDown => (MouseButton.Middle, true),
            WmMButtonUp => (MouseButton.Middle, false),
            WmXButtonDown => ((m.MouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2, true),
            WmXButtonUp => ((m.MouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2, false),
            _ => (MouseButton.None, false),
        };
        if (b == MouseButton.None) return false;

        lock (_gate)
        {
            if (_point is not null && down && b == MouseButton.Left)
            {
                _point.TrySetResult((m.Pt.X, m.Pt.Y));
                _point = null;
                _swallowPointUp = true;
                return true;
            }
            if (_swallowPointUp && !down && b == MouseButton.Left)
            {
                _swallowPointUp = false;
                return true;
            }
            // capture : clic molette et boutons latéraux (le clic gauche/droit sert à se servir de l'appli)
            if (_capture is not null && down && b is MouseButton.Middle or MouseButton.X1 or MouseButton.X2)
            {
                var tcs = _capture;
                _capture = null;
                _swallowedButtons.Add(b);
                tcs.TrySetResult(Binding.Mouse(b, CurrentMods()));
                return true;
            }
            if (!down && _swallowedButtons.Remove(b))
            {
                Fire(true, (int)b, null, false);
                return true;
            }
            if (down && Match(true, (int)b) is { } id)
            {
                _swallowedButtons.Add(b);
                Fire(true, (int)b, id, true);
                return true;
            }
        }
        sink?.Button(now, b, down, m.Pt.X, m.Pt.Y);
        return false;
    }

    private string? Match(bool mouse, int code) => Match(_bindings, mouse, code, CurrentMods());

    /// <summary>Raccourci correspondant : d'abord exactement (même touche, mêmes modificateurs), sinon un raccourci
    /// sans modificateur sur la même touche, quels que soient les modificateurs tenus (F8 arrête tout même avec Maj
    /// enfoncé).</summary>
    internal static string? Match(IReadOnlyDictionary<string, Binding> bindings, bool mouse, int code, Mods mods)
    {
        foreach (var (id, b) in bindings)
            if (b.IsMouse == mouse && b.Code == code && b.Mods == mods) return id;
        foreach (var (id, b) in bindings)
            if (b.IsMouse == mouse && b.Code == code && b.Mods == Mods.None) return id;
        return null;
    }

    /// <summary>Raccourci appuyé (down) ou relâché : un raccourci n'est signalé relâché que s'il avait été appuyé.</summary>
    private void Fire(bool mouse, int code, string? id, bool down)
    {
        if (!down)
        {
            id = _held.FirstOrDefault(h => _bindings.TryGetValue(h, out var b) && b.IsMouse == mouse && b.Code == code);
            if (id is null || !_held.Remove(id)) return;
        }
        else if (id is null || !_held.Add(id)) return;
        string fired = id;
        _events.Add(() => Hotkey?.Invoke(fired, down));
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _events.CompleteAdding();
    }
}

/// <summary>Modificateurs tenus physiquement, suivis dans le hook clavier à partir des seuls évènements non simulés.</summary>
internal sealed class PhysicalMods
{
    private readonly HashSet<int> _down = [];

    public void Update(int vk, bool down)
    {
        if (!KeyNames.IsModifier(vk)) return;
        if (down) _down.Add(vk);
        else _down.Remove(vk);
    }

    public void Clear() => _down.Clear();

    /// <summary><paramref name="isDown"/> : état réel de la touche ; si Windows la dit relâchée, elle l'est aussi
    /// physiquement (rattrape un relâchement manqué, verrouillage avec Win+L par exemple).</summary>
    public Mods Current(Func<int, bool>? isDown = null)
    {
        if (isDown is not null) _down.RemoveWhere(vk => !isDown(vk));
        var m = Mods.None;
        foreach (var vk in _down)
            m |= vk switch
            {
                0x11 or 0xA2 or 0xA3 => Mods.Ctrl,
                0x10 or 0xA0 or 0xA1 => Mods.Shift,
                0x12 or 0xA4 or 0xA5 => Mods.Alt,
                _ => Mods.Win,
            };
        return m;
    }
}
