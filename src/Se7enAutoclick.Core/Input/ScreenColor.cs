using System.Runtime.InteropServices;

namespace Se7enAutoclick.Core.Input;

/// <summary>Couleur d'un point de l'écran (0xRRGGBB), lue sur l'image affichée (jeux en fenêtre ou plein écran fenêtré).</summary>
public static class ScreenColor
{
    /// <summary>Tests : remplace la lecture de l'écran.</summary>
    internal static Func<int, int, int>? Redirect;

    public static int At(int x, int y)
    {
        if (Redirect is { } r) return r(x, y);
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            uint bgr = GetPixel(dc, x, y);
            if (bgr == 0xFFFFFFFF) return -1; // hors écran
            return (int)(((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>Même couleur, à <paramref name="tolerance"/> près sur chaque canal (0 = exacte).</summary>
    public static bool Near(int rgb, int target, int tolerance)
    {
        if (rgb < 0) return false;
        int dr = Math.Abs(((rgb >> 16) & 0xFF) - ((target >> 16) & 0xFF));
        int dg = Math.Abs(((rgb >> 8) & 0xFF) - ((target >> 8) & 0xFF));
        int db = Math.Abs((rgb & 0xFF) - (target & 0xFF));
        return Math.Max(dr, Math.Max(dg, db)) <= tolerance;
    }

    public static string Hex(int rgb) => rgb < 0 ? "—" : $"#{rgb & 0xFFFFFF:X6}";

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}

public enum ColorMode { Off, WhenMatches, WhenDifferent }

/// <summary>« Cliquer seulement si le point (X, Y) a (ou n'a plus) cette couleur ».</summary>
public sealed class ColorCondition
{
    public ColorMode Mode { get; set; } = ColorMode.Off;
    public int X { get; set; }
    public int Y { get; set; }
    public int Color { get; set; } = -1;
    public int Tolerance { get; set; } = 24;

    public bool IsSet => Mode != ColorMode.Off && Color >= 0;

    public bool Satisfied()
    {
        if (!IsSet) return true;
        bool near = ScreenColor.Near(ScreenColor.At(X, Y), Color, Tolerance);
        return Mode == ColorMode.WhenMatches ? near : !near;
    }
}
