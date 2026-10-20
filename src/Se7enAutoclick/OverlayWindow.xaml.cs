using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Se7enAutoclick;

/// <summary>
/// Petite pastille d'état en haut de l'écran, par-dessus le jeu. Elle ne prend jamais le clavier ni les clics
/// (fenêtre « transparente » pour la souris) : le jeu ne voit aucune différence.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;
    private readonly DispatcherTimer _flashTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        _flashTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            FlashText.Visibility = Visibility.Collapsed;
            _flashTimer!.Stop();
        }, Dispatcher);
        _flashTimer.Stop();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered);
        };
        SizeChanged += (_, _) => Place();
    }

    public void ShowAtTop()
    {
        Show();
        Place();
    }

    /// <summary>Message bref sous l'état (visible seulement quand la pastille l'est).</summary>
    public void Flash(string message)
    {
        FlashText.Text = message;
        FlashText.Visibility = Visibility.Visible;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 6;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
