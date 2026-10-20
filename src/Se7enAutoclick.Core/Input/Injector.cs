using System.Runtime.InteropServices;
using static Se7enAutoclick.Core.Input.Native;

namespace Se7enAutoclick.Core.Input;

/// <summary>Boutons de souris (numérotés comme dans les macros).</summary>
public enum MouseButton { None = 0, Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

/// <summary>
/// Envoie les clics et les touches à Windows (SendInput). Les touches partent avec leur code matériel (scan code) :
/// Roblox et la plupart des jeux lisent le clavier physique et ignorent les simples codes de touche.
/// </summary>
public static class Injector
{
    /// <summary>Marque posée sur nos entrées simulées (en plus du drapeau « injecté » de Windows).</summary>
    public static readonly IntPtr Signature = new(0x5E7E7A1C);

    private static readonly int Size = Marshal.SizeOf<InputData>();

    public static void Key(int vk, bool down, int scan = 0, bool extended = false)
    {
        if (scan == 0)
        {
            // touche réglée à la main (pas enregistrée) : on retrouve son code matériel
            scan = (int)MapVirtualKey((uint)vk, 0); // MAPVK_VK_TO_VSC
            if (vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5)
                extended = true; // flèches, Début/Fin, Page, Inser, Suppr, Windows, Ctrl/Alt droits…
        }
        uint flags = (down ? 0 : KeyEventKeyUp) | (extended ? KeyEventExtended : 0);
        var ki = new KeybdInput { Vk = (ushort)vk, ExtraInfo = Signature };
        if (scan != 0)
        {
            ki.Scan = (ushort)scan;
            flags |= KeyEventScanCode;
        }
        ki.Flags = flags;
        Send(new InputData { Type = InputKeyboard, U = new InputUnion { Ki = ki } });
    }

    public static void Button(MouseButton button, bool down)
    {
        (uint flags, int data) = button switch
        {
            MouseButton.Left => (down ? MouseLeftDown : MouseLeftUp, 0),
            MouseButton.Right => (down ? MouseRightDown : MouseRightUp, 0),
            MouseButton.Middle => (down ? MouseMiddleDown : MouseMiddleUp, 0),
            MouseButton.X1 => (down ? MouseXDown : MouseXUp, 1),
            MouseButton.X2 => (down ? MouseXDown : MouseXUp, 2),
            _ => (0u, 0),
        };
        if (flags == 0) return;
        Mouse(new MouseInput { Flags = flags, MouseData = data });
    }

    /// <summary>Déplacement relatif (comme une vraie souris) : c'est ce que lit la caméra des jeux 3D.</summary>
    public static void MoveBy(int dx, int dy)
    {
        if (dx != 0 || dy != 0) Mouse(new MouseInput { Dx = dx, Dy = dy, Flags = MouseMove });
    }

    /// <summary>Place le curseur à un point de l'écran (pixels du bureau virtuel, tous écrans).</summary>
    public static void MoveTo(int x, int y)
    {
        var (nx, ny) = Normalize(x, y);
        Mouse(new MouseInput { Dx = nx, Dy = ny, Flags = MouseMove | MouseAbsolute | MouseVirtualDesk });
    }

    public static void Wheel(int delta, bool horizontal = false) =>
        Mouse(new MouseInput { MouseData = delta, Flags = horizontal ? MouseHWheel : MouseWheel });

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public static IntPtr Foreground() => GetForegroundWindow();

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Coordonnées 0..65535 du bureau virtuel.</summary>
    public static (int X, int Y) Normalize(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int w = Math.Max(1, GetSystemMetrics(78)), h = Math.Max(1, GetSystemMetrics(79));
        return ((int)Math.Round((x - left) * 65535.0 / Math.Max(1, w - 1)), (int)Math.Round((y - top) * 65535.0 / Math.Max(1, h - 1)));
    }

    private static void Mouse(MouseInput mi)
    {
        mi.ExtraInfo = Signature;
        Send(new InputData { Type = InputMouse, U = new InputUnion { Mi = mi } });
    }

    private static readonly InputData[] One = new InputData[1];

    /// <summary>Tests : reçoit les entrées à la place de Windows (rien n'est envoyé au PC).</summary>
    internal static Action<InputData>? Redirect;

    private static void Send(InputData input)
    {
        if (Redirect is { } r) { r(input); return; }
        lock (One)
        {
            One[0] = input;
            SendInput(1, One, Size);
        }
    }
}
