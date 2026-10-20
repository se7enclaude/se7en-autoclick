using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Se7enAutoclick.Core;
using Se7enAutoclick.Core.Input;
using Se7enAutoclick.Core.Macros;

namespace Se7enAutoclick.ViewModels;

/// <summary>Un raccourci réglable dans la page Réglages.</summary>
public sealed class BindingItem(string id) : Observable
{
    private string _display = "";
    private bool _capturing;

    public string Id { get; } = id;
    public string Label => Actions.Label(Id);
    public string Display { get => _display; set => Set(ref _display, value); }
    public bool Capturing { get => _capturing; set => Set(ref _capturing, value); }
}

/// <summary>Une macro de la bibliothèque.</summary>
public sealed class MacroItem(string name, Macro? macro) : Observable
{
    public string Name { get; } = name;
    public Macro? Macro { get; } = macro;
    public string Summary => Macro is null ? "Fichier illisible"
        : $"{TimeSpan.FromMilliseconds(Macro.DurationMs):mm\\:ss} · {Macro.KeyPresses} touches · {Macro.Clicks} clics";
}

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly InputHub _hub = new();
    private readonly Clicker _clicker = new();
    private readonly Player _player = new();
    private readonly MacroLibrary _library = new(AppSettings.MacrosFolder);
    private readonly DispatcherTimer _tick;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private Recorder? _recorder;
    private double _recordStart;
    private string _lastSaved = "";

    public AppSettings Settings { get; } = AppSettings.Load();
    public ClickerSettings Click => Settings.Clicker;
    public PlaybackSettings Play => Settings.Playback;

    public ObservableCollection<BindingItem> Bindings { get; } = [];
    public ObservableCollection<MacroItem> Macros { get; } = [];
    public ObservableCollection<ScreenPoint> Points { get; } = [];
    public ObservableCollection<string> Steps { get; } = [];

    /// <summary>Message à afficher (toast).</summary>
    public event Action<string>? Notify;

    public MainViewModel()
    {
        foreach (var id in Actions.All) Bindings.Add(new BindingItem(id));
        foreach (var p in Click.Points) Points.Add(p);
        RefreshBindings();
        RefreshMacros(Settings.CurrentMacro);
        _lastSaved = System.Text.Json.JsonSerializer.Serialize(Settings);

        _hub.Hotkey += (id, down) => _ui.BeginInvoke(() => OnHotkey(id, down));
        _hub.EmergencyStop += () => _ui.BeginInvoke(() => StopAll("Arrêt d'urgence (Échap maintenu)"));
        _clicker.Stopped += () => _ui.BeginInvoke(() => { Raise(nameof(ClickerRunning)); Raise(nameof(Status)); });
        _player.Stopped += reason => _ui.BeginInvoke(() =>
        {
            Raise(nameof(IsPlaying));
            Raise(nameof(Status));
            Beep(false);
            if (reason is not null) Notify?.Invoke(reason);
        });

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        _tick.Start();
    }

    // ------------------------------------------------------------------ état
    public bool ClickerRunning => _clicker.IsRunning;
    public bool IsRecording => _recorder is not null;
    public bool IsPlaying => _player.IsRunning;
    public bool Busy => ClickerRunning || IsRecording || IsPlaying;

    public string ClickCount => _clicker.Clicks.ToString("N0");
    public string MeasuredCps => ClickerRunning ? _clicker.MeasuredCps.ToString("0") : "0";
    public string RecordTime => IsRecording ? TimeSpan.FromMilliseconds(_hub.NowMs - _recordStart).ToString(@"mm\:ss") : "00:00";
    public string RecordEvents => (_recorder?.Count ?? 0).ToString("N0");
    public double PlayProgress => IsPlaying ? _player.Progress : 0;
    public string PlayLoop => IsPlaying ? $"{_player.Loop}" + (Play.Loop == LoopMode.Count ? $" / {Play.LoopCount}" : "") : "—";

    /// <summary>Texte de l'overlay et de la barre du haut.</summary>
    public string Status =>
        IsRecording ? $"● ENREGISTREMENT  {RecordTime}"
        : IsPlaying ? $"▶ MACRO  boucle {PlayLoop}"
        : ClickerRunning ? $"AUTOCLIC  {MeasuredCps} clics/s"
        : "PRÊT";

    public string Version => "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    private void Tick()
    {
        if (ClickerRunning) { Raise(nameof(ClickCount)); Raise(nameof(MeasuredCps)); }
        if (IsRecording) { Raise(nameof(RecordTime)); Raise(nameof(RecordEvents)); }
        if (IsPlaying) { Raise(nameof(PlayProgress)); Raise(nameof(PlayLoop)); }
        Raise(nameof(Status));
        Raise(nameof(Busy));
        SaveIfChanged();
    }

    private void SaveIfChanged()
    {
        Click.Points = Points.ToList();
        string json = System.Text.Json.JsonSerializer.Serialize(Settings);
        if (json == _lastSaved) return;
        _lastSaved = json;
        Settings.Save();
    }

    // ------------------------------------------------------------------ raccourcis
    private void OnHotkey(string id, bool down)
    {
        switch (id)
        {
            case Actions.Clicker when Click.Trigger == ClickTrigger.Hold:
                if (down) StartClicker(); else StopClicker();
                break;
            case Actions.Clicker when down:
                ToggleClicker();
                break;
            case Actions.Record when down:
                ToggleRecording(byClick: false);
                break;
            case Actions.Play when down:
                TogglePlayback(fromWindow: false);
                break;
            case Actions.Stop when down:
                StopAll(null);
                break;
        }
    }

    private void RefreshBindings()
    {
        foreach (var b in Bindings)
            b.Display = Settings.Bindings.TryGetValue(b.Id, out var k) && k is not null ? k.Display : "—";
        _hub.SetBindings(Settings.Bindings);
        Raise(nameof(ClickerKey));
        Raise(nameof(RecordKey));
        Raise(nameof(PlayKey));
        Raise(nameof(StopKey));
        Raise(nameof(StopButtonText));
    }

    public string ClickerKey => KeyOf(Actions.Clicker);
    public string RecordKey => KeyOf(Actions.Record);
    public string PlayKey => KeyOf(Actions.Play);
    public string StopKey => KeyOf(Actions.Stop);
    public string StopButtonText => $"Tout arrêter · {StopKey}";
    private string KeyOf(string id) => Settings.Bindings.TryGetValue(id, out var b) && b is not null ? b.Display : "—";

    public async void CaptureBinding(BindingItem item)
    {
        foreach (var b in Bindings) b.Capturing = false;
        item.Capturing = true;
        item.Display = "Appuie sur une touche…";
        _hub.SetBindings(new Dictionary<string, Binding?>()); // pendant la capture, plus aucun raccourci actif
        var result = await _hub.CaptureAsync();
        item.Capturing = false;
        if (result is not null)
        {
            // un même raccourci ne sert qu'à une action
            foreach (var (id, b) in Settings.Bindings.ToList())
                if (b == result && id != item.Id) Settings.Bindings[id] = null;
            Settings.Bindings[item.Id] = result;
        }
        RefreshBindings();
    }

    public void ClearBinding(BindingItem item)
    {
        Settings.Bindings[item.Id] = null;
        RefreshBindings();
    }

    public void ResetBindings()
    {
        Settings.Bindings = AppSettings.DefaultBindings();
        RefreshBindings();
    }

    // ------------------------------------------------------------------ autoclic
    public double Cps
    {
        get => Math.Round(Click.Cps, 2);
        set
        {
            Click.IntervalMs = Math.Round(1000.0 / Math.Clamp(value, 0.01, 2000), 3);
            Raise();
            Raise(nameof(IntervalMs));
        }
    }

    public double IntervalMs
    {
        get => Click.IntervalMs;
        set
        {
            Click.IntervalMs = Math.Clamp(value, 0.5, 86_400_000);
            Raise();
            Raise(nameof(Cps));
        }
    }

    public string ClickTarget => Click.UseKey ? KeyNames.Of(Click.KeyVk) : Binding.MouseName(Click.Button);

    public bool UseKey
    {
        get => Click.UseKey;
        set { Click.UseKey = value; Raise(); Raise(nameof(ClickTarget)); }
    }

    public MouseButton ClickButton
    {
        get => Click.Button;
        set { Click.Button = value; Raise(); Raise(nameof(ClickTarget)); }
    }

    private bool _capturingKey;
    public bool CapturingKey { get => _capturingKey; private set { Set(ref _capturingKey, value); Raise(nameof(ClickKeyText)); } }
    public string ClickKeyText => CapturingKey ? "Appuie sur une touche…" : KeyNames.Of(Click.KeyVk);

    public async void CaptureClickKey()
    {
        CapturingKey = true;
        _hub.SetBindings(new Dictionary<string, Binding?>());
        var b = await _hub.CaptureAsync();
        if (b is { IsMouse: false }) Click.KeyVk = b.Code;
        CapturingKey = false;
        RefreshBindings();
        Raise(nameof(ClickTarget));
    }

    private bool _pickingPoint;
    public bool PickingPoint { get => _pickingPoint; private set => Set(ref _pickingPoint, value); }

    /// <summary>Le prochain clic gauche à l'écran devient un point (Échap annule).</summary>
    public async void PickPoint(bool replace)
    {
        PickingPoint = true;
        Notify?.Invoke("Clique à l'endroit voulu (Échap pour annuler)");
        var p = await _hub.CapturePointAsync();
        PickingPoint = false;
        if (p is not { } pt) return;
        if (replace) Points.Clear();
        Points.Add(new ScreenPoint { X = pt.X, Y = pt.Y });
        if (Click.Position == ClickPosition.Cursor) { Click.Position = replace ? ClickPosition.Fixed : ClickPosition.Points; Raise(nameof(Click)); }
    }

    public void ToggleClicker()
    {
        if (ClickerRunning) StopClicker();
        else StartClicker();
    }

    public void StartClicker()
    {
        if (ClickerRunning) return;
        if (Click.Position != ClickPosition.Cursor && Points.Count == 0)
        {
            Notify?.Invoke("Choisis d'abord un point à l'écran (bouton « Choisir »)");
            return;
        }
        Click.Points = Points.ToList();
        _clicker.Start(Click);
        Beep(true);
        Raise(nameof(ClickerRunning));
    }

    public void StopClicker()
    {
        if (!ClickerRunning) return;
        _clicker.Stop();
        Beep(false);
        Raise(nameof(ClickerRunning));
        Raise(nameof(ClickCount));
    }

    // ------------------------------------------------------------------ macros
    private MacroItem? _selected;

    public MacroItem? SelectedMacro
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Settings.CurrentMacro = value?.Name;
            Steps.Clear();
            if (value?.Macro is { } m)
                foreach (var s in m.Steps().Take(2000)) Steps.Add(s);
            Raise(nameof(HasMacro));
        }
    }

    public bool HasMacro => SelectedMacro?.Macro is not null;

    public void RefreshMacros(string? select = null)
    {
        select ??= SelectedMacro?.Name;
        Macros.Clear();
        foreach (var name in _library.Names())
        {
            Macro? m = null;
            try { m = _library.Load(name); } catch (Exception) { }
            Macros.Add(new MacroItem(name, m));
        }
        SelectedMacro = Macros.FirstOrDefault(m => m.Name == select) ?? Macros.FirstOrDefault();
    }

    public void ToggleRecording(bool byClick)
    {
        if (IsRecording) StopRecording(byClick);
        else StartRecording();
    }

    public void StartRecording()
    {
        if (IsRecording || IsPlaying) return;
        StopClicker();
        _recordStart = _hub.NowMs;
        _recorder = new Recorder(_recordStart);
        _hub.StartRecording(_recorder);
        Beep(true);
        Raise(nameof(IsRecording));
        Notify?.Invoke($"Enregistrement · joue normalement, puis {RecordKey} pour arrêter");
    }

    public void StopRecording(bool byClick)
    {
        if (_recorder is not { } rec) return;
        _hub.StopRecording();
        _recorder = null;
        Beep(false);
        Raise(nameof(IsRecording));
        var macro = rec.Finish(_library.NextName(), _hub.NowMs, byClick);
        if (macro.Events.All(e => e.Kind is EventKind.MoveBy or EventKind.MoveTo) && macro.Events.Count < 50)
        {
            Notify?.Invoke("Rien n'a été enregistré.");
            return;
        }
        _library.Save(macro);
        RefreshMacros(macro.Name);
        Notify?.Invoke($"« {macro.Name} » enregistrée · {PlayKey} pour la rejouer en boucle");
    }

    public void TogglePlayback(bool fromWindow)
    {
        if (IsPlaying) { _player.Stop(); return; }
        if (IsRecording) return;
        if (SelectedMacro?.Macro is not { } macro)
        {
            Notify?.Invoke("Aucune macro : enregistre-en une d'abord.");
            return;
        }
        StopClicker();
        var settings = Play;
        if (fromWindow && settings.CountdownSeconds < 3)
        {
            // lancée depuis la fenêtre : 3 s pour revenir dans le jeu
            settings = System.Text.Json.JsonSerializer.Deserialize<PlaybackSettings>(System.Text.Json.JsonSerializer.Serialize(Play))!;
            settings.CountdownSeconds = 3;
            Notify?.Invoke("Lecture dans 3 secondes · retourne dans ton jeu");
        }
        _player.Start(macro, settings);
        Beep(true);
        Raise(nameof(IsPlaying));
    }

    public void RenameMacro(MacroItem item, string name)
    {
        try
        {
            _library.Rename(item.Name, name);
            RefreshMacros(MacroLibrary.SafeName(name));
        }
        catch (Exception ex) { Notify?.Invoke(ex.Message); }
    }

    public void DuplicateMacro(MacroItem item) => RefreshMacros(_library.Duplicate(item.Name));

    public void DeleteMacro(MacroItem item)
    {
        _library.Delete(item.Name);
        RefreshMacros();
    }

    public void OpenMacrosFolder()
    {
        System.IO.Directory.CreateDirectory(_library.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.Folder}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ général
    public void StopAll(string? message)
    {
        bool any = Busy;
        StopClicker();
        if (IsRecording) StopRecording(false);
        if (IsPlaying) _player.Stop();
        _hub.CancelCapture();
        if (any && message is not null) Notify?.Invoke(message);
    }

    public bool AutostartEnabled
    {
        get => Services.Autostart.IsEnabled;
        set { Services.Autostart.Set(value); Raise(); }
    }

    private void Beep(bool on)
    {
        if (!Settings.Sounds) return;
        Task.Run(() => { try { Console.Beep(on ? 1320 : 880, 60); } catch (Exception) { } });
    }

    public void Dispose()
    {
        _tick.Stop();
        StopAll(null);
        SaveIfChanged();
        _hub.Dispose();
    }
}
