using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Se7enAutoclick;

public partial class App : Application
{
    private const string InstanceName = "se7enAutoclick.SingleInstance";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Appelé par Seventh OS avant la désinstallation : on retire le lancement avec Windows.
        if (e.Args.Contains("--uninstall-cleanup"))
        {
            try { Services.Autostart.Set(false); } catch (Exception) { }
            Shutdown(0);
            return;
        }

        // Une seule instance : si l'app tourne déjà, on la ramène au premier plan.
        _mutex = new Mutex(true, InstanceName, out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        // Plantage : tout s'arrête (aucune touche ne reste enfoncée), journal crash.log et message.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => OnCrash(ev.ExceptionObject as Exception, fatal: ev.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, ev) => { ev.SetObserved(); WriteCrashLog(ev.Exception); };

        base.OnStartup(e);
        bool minimized = e.Args.Contains("--minimized");
        if (minimized)
        {
            StartMain(true);
            return;
        }
        // Animation de lancement, puis la fenêtre principale (pas d'animation au démarrage avec Windows).
        var splash = new LaunchSplash();
        splash.Done += (_, _) => StartMain(false);
        splash.Show();
    }

    private void StartMain(bool minimized)
    {
        var window = new MainWindow(startMinimized: minimized);
        MainWindow = window;
        _vm = window.ViewModel;
        if (!minimized) window.Activate();

        var signal = _showSignal!;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "show-signal" }.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // l'appli reste ouverte, mais tout est arrêté
        OnCrash(e.Exception, fatal: false);
    }

    private int _crashing;
    private volatile ViewModels.MainViewModel? _vm; // lisible depuis n'importe quel fil

    private void OnCrash(Exception? ex, bool fatal)
    {
        // d'abord relâcher : c'est ce qui compte si la suite échoue
        try { _vm?.EmergencyRelease(); } catch (Exception) { }
        string? log = WriteCrashLog(ex);
        if (Interlocked.Exchange(ref _crashing, 1) == 1) return; // un seul message à la fois
        try
        {
            string text = (fatal ? "se7en Autoclick a rencontré une erreur et va se fermer." : "se7en Autoclick a rencontré une erreur.")
                          + "\n\nL'autoclic et les macros ont été arrêtés, plus aucune touche n'est enfoncée."
                          + (ex is null ? "" : "\n\n" + ex.Message)
                          + (log is null ? "" : "\n\nDétails : " + log);
            void Show() => MessageBox.Show(text, "se7en Autoclick", MessageBoxButton.OK, MessageBoxImage.Error);
            if (Dispatcher.CheckAccess()) Show();
            else if (fatal) Show(); // le fil d'interface ne reprendra peut-être pas la main
            else Dispatcher.Invoke(Show);
        }
        catch (Exception) { }
        finally { Interlocked.Exchange(ref _crashing, 0); }
    }

    /// <summary>Ajoute l'erreur à crash.log dans le dossier de données de l'appli ; renvoie son chemin.</summary>
    private static string? WriteCrashLog(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Core.AppSettings.Folder);
            string path = Path.Combine(Core.AppSettings.Folder, "crash.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path); // pas de fichier sans fin
            string version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
            File.AppendAllText(path, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · v{version}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch (Exception) { return null; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
