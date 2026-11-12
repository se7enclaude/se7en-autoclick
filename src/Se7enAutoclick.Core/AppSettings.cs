using System.Text.Json;
using Se7enAutoclick.Core.Input;
using Se7enAutoclick.Core.Macros;

namespace Se7enAutoclick.Core;

/// <summary>Identifiants des raccourcis de l'appli.</summary>
public static class Actions
{
    public const string Clicker = "clicker";
    public const string Record = "record";
    public const string Play = "play";
    public const string Stop = "stop";

    public static readonly string[] All = [Clicker, Record, Play, Stop];

    public static string Label(string id) => id switch
    {
        Clicker => "Autoclic (démarrer / arrêter)",
        Record => "Enregistrer une macro / arrêter",
        Play => "Lire la macro / arrêter",
        Stop => "Arrêt d'urgence (tout)",
        _ => id,
    };
}

/// <summary>Réglages mémorisés entre deux lancements.</summary>
public sealed class AppSettings
{
    public ClickerSettings Clicker { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public AntiAfkSettings AntiAfk { get; set; } = new();

    public Dictionary<string, Binding?> Bindings { get; set; } = DefaultBindings();

    /// <summary>Macro choisie pour la lecture (nom du fichier, sans extension).</summary>
    public string? CurrentMacro { get; set; }

    public bool ShowOverlay { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool Sounds { get; set; } = true;

    public static Dictionary<string, Binding?> DefaultBindings() => new()
    {
        [Actions.Clicker] = Binding.Key(0x75), // F6
        [Actions.Record] = Binding.Key(0x78),  // F9
        [Actions.Play] = Binding.Key(0x79),    // F10
        [Actions.Stop] = Binding.Key(0x77),    // F8
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Se7enAutoclick");

    public static string MacrosFolder => Path.Combine(Folder, "Macros");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Message pour l'utilisateur si le dernier chargement a dû repartir des réglages par défaut.</summary>
    public static string? LoadProblem { get; private set; }

    public static AppSettings Load() => Load(FilePath);

    /// <summary>Charge les réglages. Un fichier illisible (abîmé) est mis de côté en « .corrompu » avant de repartir
    /// des réglages par défaut : la prochaine sauvegarde ne l'écrase pas sans laisser de trace.</summary>
    public static AppSettings Load(string path)
    {
        LoadProblem = null;
        string text;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            LoadProblem = "Réglages illisibles (" + ex.Message + ") : réglages par défaut pour cette fois.";
            return new AppSettings();
        }
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? throw new JsonException("fichier vide");
            s.Bindings ??= [];
            foreach (var (id, b) in DefaultBindings())
                s.Bindings.TryAdd(id, b); // raccourcis ajoutés par une mise à jour
            s.Clicker ??= new();
            s.Playback ??= new();
            s.AntiAfk ??= new();
            return s;
        }
        catch (Exception)
        {
            string aside = path + ".corrompu";
            try { File.Move(path, aside, true); }
            catch (Exception) { aside = "(impossible de le mettre de côté)"; }
            LoadProblem = $"Réglages abîmés : réglages par défaut. L'ancien fichier est gardé dans {aside}";
            return new AppSettings();
        }
    }

    /// <summary>Enregistre les réglages (fichier temporaire puis remplacement). Lève une exception en cas d'échec.</summary>
    public void Save() => Save(FilePath);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}
