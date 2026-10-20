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
/// Nos propres entrées simulées (et celles des autres logiciels) sont ignorées.
/// </summary>
public sealed class InputHub : IDisposable
{
    private const uint WmStartRaw = WmApp + 1, WmStopRaw = WmApp + 2;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private uint _threadId;
    private IntPtr _kbHook, _msHook, _rawWindow;
    private HookProc? _kbProc, _msProc;
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
        return tcs.Task;
    }

    /// <summary>Commence à envoyer les entrées à <paramref name="sink"/> (enregistrement).</summary>
    public void StartRecording(IInputSink sink)
    {
        _sink = sink;
        PostThreadMessage(_threadId, WmStartRaw, IntPtr.Zero, IntPtr.Zero);
    }

    public void StopRecording()
    {
        _sink = null;
        PostThreadMessage(_threadId, WmStopRaw, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ fil des hooks
    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
        _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc, module, 0);
        _msHook = SetWindowsHookEx(WhMouseLl, _msProc, module, 0);
        _rawWindow = CreateRawWindow(module);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Hwnd == IntPtr.Zero && msg.Message == WmStartRaw) SetRaw(true);
            else if (msg.Hwnd == IntPtr.Zero && msg.Message == WmStopRaw) SetRaw(false);
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

    private static Mods CurrentMods()
    {
        var m = Mods.None;
        if (Injector.IsDown(0x11)) m |= Mods.Ctrl;
        if (Injector.IsDown(0x10)) m |= Mods.Shift;
        if (Injector.IsDown(0x12)) m |= Mods.Alt;
        if (Injector.IsDown(0x5B) || Injector.IsDown(0x5C)) m |= Mods.Win;
        return m;
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KbdLlHook>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WmKeyDown or WmSysKeyDown;
            bool injected = (k.Flags & LlkhfInjected) != 0;
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
                return true;
            }
            if (_capture is not null && down && !KeyNames.IsModifier(vk))
            {
                var tcs = _capture;
                _capture = null;
                _swallowedKeys.Add(vk);
                tcs.TrySetResult(vk == 0x1B ? null : Binding.Key(vk, CurrentMods()));
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
            if ((m.Flags & LlmhfInjected) == 0 && HandleMouse(wParam.ToInt32(), m))
                return 1;
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

    /// <summary>Raccourci correspondant exactement (même touche, mêmes modificateurs).</summary>
    private string? Match(bool mouse, int code)
    {
        var mods = CurrentMods();
        foreach (var (id, b) in _bindings)
            if (b.IsMouse == mouse && b.Code == code && b.Mods == mods) return id;
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
