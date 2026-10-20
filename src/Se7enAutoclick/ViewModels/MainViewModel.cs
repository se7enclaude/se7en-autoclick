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

/// <summary>Une ligne de l'enchaînement : une macro et son nombre de répétitions.</summary>
public sealed class ChainItem(ChainEntry entry) : Observable
{
    public ChainEntry Entry { get; } = entry;
    public string Name => Entry.Macro;
    public int Repeat
    {
        get => Entry.Repeat;
        set { Entry.Repeat = Math.Clamp(value, 1, 100_000); Raise(); }
    }
}

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly InputHub _hub = new();
    private readonly Clicker _clicker = new();
    private readonly Player _player = new();
    private readonly AntiAfk _antiAfk = new();
    private readonly Stack<Macro> _undo = new();
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
    public ObservableCollection<ChainItem> Chain { get; } = [];
    public AntiAfkSettings Afk => Settings.AntiAfk;

    /// <summary>Message à afficher (toast).</summary>
    public event Action<string>? Notify;

    public MainViewModel()
    {
        foreach (var id in Actions.All) Bindings.Add(new BindingItem(id));
        foreach (var p in Click.Points) Points.Add(p);
        RefreshBindings();
        RefreshMacros(Settings.CurrentMacro);
        foreach (var c in Play.Chain) Chain.Add(new ChainItem(c));
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

        _antiAfk.IsBusy = () => _clicker.IsRunning || _player.IsRunning;
        _antiAfk.Done += msg => _ui.BeginInvoke(() => { Notify?.Invoke(msg); Raise(nameof(AfkNext)); });
        _antiAfk.Apply(Afk);

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
    public string PlayCurrent => IsPlaying ? _player.Current : "";

    /// <summary>Texte de l'overlay et de la barre du haut.</summary>
    public string Status =>
        IsRecording ? $"● ENREGISTREMENT  {RecordTime}"
        : IsPlaying && _player.Waiting ? $"⏳ {PlayCurrent}  attente couleur"
        : IsPlaying ? $"▶ {PlayCurrent}  boucle {PlayLoop}"
        : ClickerRunning ? $"AUTOCLIC  {MeasuredCps} clics/s"
        : "PRÊT";

    public string Version => "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    private void Tick()
    {
        if (ClickerRunning) { Raise(nameof(ClickCount)); Raise(nameof(MeasuredCps)); }
        if (IsRecording) { Raise(nameof(RecordTime)); Raise(nameof(RecordEvents)); }
        if (IsPlaying) { Raise(nameof(PlayProgress)); Raise(nameof(PlayLoop)); Raise(nameof(PlayCurrent)); }
        if (Afk.Enabled) Raise(nameof(AfkNext));
        Raise(nameof(Status));
        Raise(nameof(Busy));
        SaveIfChanged();
    }

    private void SaveIfChanged()
    {
        Click.Points = Points.ToList();
        Play.Chain = Chain.Select(c => c.Entry).ToList();
        _antiAfk.Apply(Afk);
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

    public ColorCondition Condition => Click.Condition;
    public string ConditionText => Condition.Color < 0 ? "Aucun point choisi" : $"({Condition.X}, {Condition.Y})  {ScreenColor.Hex(Condition.Color)}";
    public System.Windows.Media.Brush ConditionBrush => Swatch(Condition.Color);

    private static System.Windows.Media.Brush Swatch(int rgb) => rgb < 0
        ? System.Windows.Media.Brushes.Transparent
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    /// <summary>Clic sur un point de l'écran : on retient sa position et sa couleur actuelle.</summary>
    private async Task<(int X, int Y, int Rgb)?> PickColorAsync()
    {
        PickingPoint = true;
        Notify?.Invoke("Clique sur le point à surveiller, quand il a la bonne couleur (Échap pour annuler)");
        var p = await _hub.CapturePointAsync();
        PickingPoint = false;
        if (p is not { } pt) return null;
        await Task.Delay(30);
        return (pt.X, pt.Y, ScreenColor.At(pt.X, pt.Y));
    }

    public async void PickConditionColor()
    {
        if (await PickColorAsync() is not { } c) return;
        Condition.X = c.X;
        Condition.Y = c.Y;
        Condition.Color = c.Rgb;
        if (Condition.Mode == ColorMode.Off) Condition.Mode = ColorMode.WhenMatches;
        Raise(nameof(Condition));
        Raise(nameof(ConditionText));
        Raise(nameof(ConditionBrush));
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
            if (_selected?.Name != value?.Name) { _undo.Clear(); Raise(nameof(CanUndo)); }
            if (!Set(ref _selected, value)) return;
            Settings.CurrentMacro = value?.Name;
            Steps.Clear();
            if (value?.Macro is { } m)
                foreach (var s in m.Steps().Take(5000)) Steps.Add(s);
            _step = -1;
            Raise(nameof(SelectedStep));
            Raise(nameof(HasStep));
            Raise(nameof(HasMacro));
        }
    }

    public bool HasMacro => SelectedMacro?.Macro is not null;

    private int _step = -1;

    /// <summary>Étape choisie dans la liste (-1 = aucune).</summary>
    public int SelectedStep
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            Raise(nameof(HasStep));
            Raise(nameof(StepDelay));
        }
    }

    public bool HasStep => _step >= 0 && SelectedMacro?.Macro is { } m && _step < MacroEditor.StepCount(m);
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Délai avant l'étape choisie, en ms (modifiable).</summary>
    public double StepDelay
    {
        get => HasStep ? Math.Round(MacroEditor.DelayBefore(SelectedMacro!.Macro!, _step)) : 0;
        set { if (HasStep) Edit(m => MacroEditor.SetDelayBefore(m, _step, value), keepStep: true); }
    }

    public void DeleteStep() { if (HasStep) Edit(m => MacroEditor.DeleteStep(m, _step), keepStep: true); }
    public void TrimBefore() { if (HasStep) Edit(m => MacroEditor.TrimBefore(m, _step), keepStep: false); }
    public void TrimAfter() { if (HasStep) Edit(m => MacroEditor.TrimAfter(m, _step), keepStep: true); }

    public async void InsertWaitColor(bool untilGone)
    {
        if (SelectedMacro?.Macro is null) return;
        int at = HasStep ? _step : MacroEditor.StepCount(SelectedMacro.Macro);
        if (await PickColorAsync() is not { } c) return;
        Edit(m => MacroEditor.InsertWaitColor(m, at, c.X, c.Y, c.Rgb, 24, untilGone), keepStep: true);
        Notify?.Invoke(untilGone ? $"Attente ajoutée · la macro attendra que ce point change de couleur ({ScreenColor.Hex(c.Rgb)})"
                                 : $"Attente ajoutée · la macro attendra que ce point soit {ScreenColor.Hex(c.Rgb)}");
    }

    public void Undo()
    {
        if (_undo.Count == 0 || SelectedMacro is null) return;
        var previous = _undo.Pop();
        _library.Save(previous);
        RefreshMacros(previous.Name);
        Raise(nameof(CanUndo));
    }

    private void Edit(Func<Macro, Macro> change, bool keepStep)
    {
        if (SelectedMacro?.Macro is not { } m || IsPlaying) return;
        int step = _step;
        try
        {
            var edited = change(m);
            _undo.Push(m);
            _library.Save(edited);
            RefreshMacros(edited.Name);
            SelectedStep = keepStep ? Math.Min(step, MacroEditor.StepCount(edited) - 1) : 0;
            Raise(nameof(CanUndo));
        }
        catch (Exception ex) { Notify?.Invoke("Modification impossible : " + ex.Message); }
    }

    // ------------------------------------------------------------------ enchaînement
    public void AddToChain()
    {
        if (SelectedMacro is null) return;
        Chain.Add(new ChainItem(new ChainEntry { Macro = SelectedMacro.Name, Repeat = 1 }));
        Play.UseChain = true;
        Raise(nameof(Play));
    }

    public void RemoveFromChain(ChainItem item) => Chain.Remove(item);

    public void MoveInChain(ChainItem item, int delta)
    {
        int i = Chain.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= Chain.Count) return;
        Chain.Move(i, j);
    }

    // ------------------------------------------------------------------ anti-AFK
    public string AfkNext => Afk.Enabled ? $"Prochain geste vers {_antiAfk.NextAt:HH:mm}" : "Éteint";
    public void TestAfk() => _antiAfk.Now();

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
        var parts = new List<ChainPart>();
        if (Play.UseChain)
        {
            foreach (var c in Chain)
            {
                try { parts.Add(new ChainPart(c.Name, _library.Load(c.Name), c.Repeat)); }
                catch (Exception) { Notify?.Invoke($"La macro « {c.Name} » de l'enchaînement n'existe plus."); return; }
            }
            if (parts.Count == 0) { Notify?.Invoke("L'enchaînement est vide : ajoute des macros avec « Ajouter »."); return; }
        }
        else if (SelectedMacro?.Macro is { } one) parts.Add(new ChainPart(SelectedMacro.Name, one, 1));
        else
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
        _player.Start(parts, settings);
        Beep(true);
        Raise(nameof(IsPlaying));
    }

    public void RenameMacro(MacroItem item, string name)
    {
        try
        {
            _library.Rename(item.Name, name);
            foreach (var c in Chain.Where(c => c.Name == item.Name).ToList())
                Chain[Chain.IndexOf(c)] = new ChainItem(new ChainEntry { Macro = MacroLibrary.SafeName(name), Repeat = c.Repeat });
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
        _antiAfk.Dispose();
        StopAll(null);
        SaveIfChanged();
        _hub.Dispose();
    }
}
