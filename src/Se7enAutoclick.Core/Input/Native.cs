using System.Runtime.InteropServices;

namespace Se7enAutoclick.Core.Input;

/// <summary>Appels Windows bruts : entrées simulées, hooks bas niveau, Raw Input, fenêtre de messages.</summary>
internal static class Native
{
    // ---------- SendInput ----------
    public const uint InputMouse = 0, InputKeyboard = 1;

    public const uint KeyEventExtended = 0x1, KeyEventKeyUp = 0x2, KeyEventUnicode = 0x4, KeyEventScanCode = 0x8;

    public const uint MouseMove = 0x1, MouseLeftDown = 0x2, MouseLeftUp = 0x4, MouseRightDown = 0x8, MouseRightUp = 0x10,
        MouseMiddleDown = 0x20, MouseMiddleUp = 0x40, MouseXDown = 0x80, MouseXUp = 0x100, MouseWheel = 0x800,
        MouseHWheel = 0x1000, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct InputData
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx, Dy;
        public int MouseData;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, InputData[] inputs, int size);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point p);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    // ---------- Hooks bas niveau ----------
    public const int WhKeyboardLl = 13, WhMouseLl = 14;
    public const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    public const int WmMouseMove = 0x200, WmLButtonDown = 0x201, WmLButtonUp = 0x202, WmRButtonDown = 0x204, WmRButtonUp = 0x205,
        WmMButtonDown = 0x207, WmMButtonUp = 0x208, WmMouseWheel = 0x20A, WmXButtonDown = 0x20B, WmXButtonUp = 0x20C,
        WmMouseHWheel = 0x20E;
    public const uint LlkhfExtended = 0x1, LlkhfInjected = 0x10, LlmhfInjected = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHook
    {
        public uint Vk, Scan, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MsLlHook
    {
        public Point Pt;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    // ---------- Boucle de messages ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public const uint WmQuit = 0x12, WmApp = 0x8000, WmInput = 0xFF;

    // ---------- Fenêtre de messages (pour Raw Input) ----------
    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint Size, Style;
        public WndProc Proc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    public static readonly IntPtr HwndMessage = new(-3);

    // ---------- Raw Input ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    public const uint RidevInputSink = 0x100, RidevRemove = 0x1, RidInput = 0x10000003, RimTypeMouse = 0;
    public const ushort MouseMoveAbsoluteFlag = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputHeader
    {
        public uint Type, Size;
        public IntPtr Device, WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawMouse
    {
        public ushort Flags;
        public ushort Padding;
        public ushort ButtonFlags, ButtonData;
        public uint RawButtons;
        public int LastX, LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputMouse
    {
        public RawInputHeader Header;
        public RawMouse Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    // ---------- Minuterie système ----------
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint ms);
}
