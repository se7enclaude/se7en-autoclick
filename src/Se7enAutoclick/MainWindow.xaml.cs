using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Se7enAutoclick.ViewModels;
using Forms = System.Windows.Forms;

namespace Se7enAutoclick;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _tray;
    private readonly OverlayWindow _overlay;
    private bool _exiting, _trayHintShown;

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.Notify += ShowToast;

        _overlay = new OverlayWindow { DataContext = _vm };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Busy) or nameof(MainViewModel.Status)) UpdateOverlay();
            if (e.PropertyName == nameof(MainViewModel.SelectedMacro)) RenameBox.Text = _vm.SelectedMacro?.Name ?? "";
        };
        RenameBox.Text = _vm.SelectedMacro?.Name ?? "";

        // Icône près de l'horloge
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir se7en Autoclick", null, (_, _) => ShowFromTray());
        menu.Items.Add("Autoclic : démarrer / arrêter", null, (_, _) => _vm.ToggleClicker());
        menu.Items.Add("Lire la macro / arrêter", null, (_, _) => _vm.TogglePlayback(fromWindow: true));
        menu.Items.Add("Tout arrêter", null, (_, _) => _vm.StopAll(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitApp());
        _tray = new Forms.NotifyIcon
        {
            Text = "se7en Autoclick",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };

        Closing += OnClosing;
        if (startMinimized)
        {
            _trayHintShown = true;
            new WindowInteropHelper(this).EnsureHandle();
        }
        else
        {
            Show();
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        return stream != null ? new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
    }

    private void UpdateOverlay()
    {
        bool show = _vm.Settings.ShowOverlay && _vm.Busy;
        if (show && !_overlay.IsVisible) _overlay.ShowAtTop();
        else if (!show && _overlay.IsVisible) _overlay.Hide();
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) => UpdateOverlay();

    // ---------- Fenêtre ----------

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting && _vm.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "se7en Autoclick tourne toujours",
                    "Tes raccourcis restent actifs. Clic droit sur l'icône près de l'horloge pour quitter.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        _overlay.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _vm.Dispose();
        Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Sans bordure Windows, une fenêtre maximisée déborde un peu de l'écran.
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageClicker == null) return;
        PageClicker.Visibility = TabClicker.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMacros.Visibility = TabMacros.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Autoclic ----------

    private async void ToggleClicker_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ClickerRunning) { _vm.StopClicker(); return; }
        // lancé depuis la fenêtre : on la réduit et on laisse 2 s pour viser, sinon on cliquerait sur le bouton
        ShowToast($"Clics dans 2 secondes · {_vm.ClickerKey} pour arrêter");
        WindowState = WindowState.Minimized;
        await Task.Delay(2000);
        _vm.StartClicker();
    }

    private void StopAll_Click(object sender, RoutedEventArgs e) => _vm.StopAll(null);
    private void CaptureClickKey_Click(object sender, RoutedEventArgs e) => _vm.CaptureClickKey();
    private void PickPoint_Click(object sender, RoutedEventArgs e) => _vm.PickPoint(replace: _vm.Click.Position != Core.ClickPosition.Points);
    private void AddPoint_Click(object sender, RoutedEventArgs e) => _vm.PickPoint(replace: false);
    private void ClearPoints_Click(object sender, RoutedEventArgs e) => _vm.Points.Clear();

    // ---------- Macros ----------

    private void Record_Click(object sender, RoutedEventArgs e) => _vm.ToggleRecording(byClick: true);

    private void Play_Click(object sender, RoutedEventArgs e) => _vm.TogglePlayback(fromWindow: true);

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenMacrosFolder();

    private void RenameMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMacro is { } m && RenameBox.Text.Trim() is { Length: > 0 } name) _vm.RenameMacro(m, name);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RenameMacro_Click(sender, e);
    }

    private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMacro is { } m) _vm.DuplicateMacro(m);
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMacro is not { } m) return;
        if (MessageBox.Show(this, $"Supprimer la macro « {m.Name} » ?", "se7en Autoclick", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.DeleteMacro(m);
    }

    // ---------- Réglages ----------

    private void CaptureBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BindingItem b) _vm.CaptureBinding(b);
    }

    private void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BindingItem b) _vm.ClearBinding(b);
    }

    private void ResetBindings_Click(object sender, RoutedEventArgs e) => _vm.ResetBindings();

    // ---------- Toast ----------

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3400))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3700)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        Toast.BeginAnimation(OpacityProperty, anim);
        _overlay.Flash(message);
    }
}
