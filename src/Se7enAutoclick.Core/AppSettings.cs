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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                foreach (var (id, b) in DefaultBindings())
                    s.Bindings.TryAdd(id, b); // raccourcis ajoutés par une mise à jour
                return s;
            }
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception) { }
    }
}
