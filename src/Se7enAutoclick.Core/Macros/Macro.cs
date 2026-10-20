using System.IO.Compression;
using System.Text.Json;
using Se7enAutoclick.Core.Input;

namespace Se7enAutoclick.Core.Macros;

public enum EventKind : byte
{
    KeyDown = 1, KeyUp = 2,
    ButtonDown = 3, ButtonUp = 4,
    Wheel = 5, HWheel = 6,
    /// <summary>Position du curseur à l'écran (A = x, B = y).</summary>
    MoveTo = 7,
    /// <summary>Déplacement brut de la souris (A = dx, B = dy) : la caméra des jeux 3D.</summary>
    MoveBy = 8,
    /// <summary>Attend qu'un point de l'écran (A = x, B = y) prenne la couleur C (0xRRGGBB) à D près ;
    /// Ext = attend au contraire qu'il la perde. La suite de la macro est décalée d'autant.</summary>
    WaitColor = 9,
}

/// <summary>
/// Un geste enregistré. <see cref="T"/> : millisecondes depuis le début.
/// Touches : A = code de touche, B = code matériel, Ext = touche étendue.
/// Boutons : A = bouton, B/C = position à l'écran. Molette : A = pas (120 = un cran), B/C = position.
/// Attente de couleur : voir <see cref="EventKind.WaitColor"/>.
/// </summary>
public readonly record struct MacroEvent(double T, EventKind Kind, int A, int B = 0, int C = 0, bool Ext = false, int D = 0)
{
    /// <summary>Geste qui apparaît dans la liste des étapes (pas les mouvements de souris).</summary>
    public bool IsStep => Kind is not (EventKind.MoveTo or EventKind.MoveBy);
}

/// <summary>Comment rejouer la souris.</summary>
public enum MouseReplay
{
    /// <summary>Déplacements bruts (comme une vraie souris) : caméra des jeux 3D comme Roblox. Recommandé.</summary>
    Relative,
    /// <summary>Positions exactes à l'écran : menus, bureau, jeux en 2D.</summary>
    Screen,
}

public sealed class Macro
{
    public const string Extension = ".se7macro";

    public string Name { get; set; } = "Macro";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<MacroEvent> Events { get; set; } = [];

    public double DurationMs => Events.Count == 0 ? 0 : Events[^1].T;

    public int KeyPresses => Events.Count(e => e.Kind == EventKind.KeyDown);
    public int Clicks => Events.Count(e => e.Kind == EventKind.ButtonDown);

    /// <summary>Résumé lisible des étapes (sans les milliers de petits déplacements de souris).</summary>
    public IEnumerable<string> Steps()
    {
        foreach (var e in Events.Where(e => e.IsStep))
        {
            string at = TimeSpan.FromMilliseconds(e.T).ToString(@"mm\:ss\.fff");
            switch (e.Kind)
            {
                case EventKind.KeyDown: yield return $"{at}  ↓ {KeyNames.Of(e.A)}"; break;
                case EventKind.KeyUp: yield return $"{at}  ↑ {KeyNames.Of(e.A)}"; break;
                case EventKind.ButtonDown: yield return $"{at}  ↓ {Binding.MouseName((MouseButton)e.A)}  ({e.B}, {e.C})"; break;
                case EventKind.ButtonUp: yield return $"{at}  ↑ {Binding.MouseName((MouseButton)e.A)}"; break;
                case EventKind.Wheel or EventKind.HWheel: yield return $"{at}  molette {(e.A > 0 ? "▲" : "▼")}"; break;
                case EventKind.WaitColor:
                    yield return $"{at}  ⏳ attendre que ({e.A}, {e.B}) {(e.Ext ? "ne soit plus" : "soit")} {Input.ScreenColor.Hex(e.C)}";
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ fichier .se7macro (JSON compressé)
    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";
        public DateTime Created { get; set; }
        /// <summary>Évènements en tableaux compacts : [t (µs), type, a, b, c, ext, d].</summary>
        public List<long[]> Events { get; set; } = [];
    }

    public void Save(string path)
    {
        var model = new FileModel
        {
            Name = Name, Created = Created,
            Events = Events.Select(e => e.D == 0
                ? new[] { (long)Math.Round(e.T * 1000), (long)e.Kind, e.A, e.B, e.C, e.Ext ? 1L : 0L }
                : new[] { (long)Math.Round(e.T * 1000), (long)e.Kind, e.A, e.B, e.C, e.Ext ? 1L : 0L, e.D }).ToList(),
        };
        string tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, model);
        File.Move(tmp, path, true);
    }

    public static Macro Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        var model = JsonSerializer.Deserialize<FileModel>(gz) ?? throw new InvalidDataException("Fichier de macro vide.");
        return new Macro
        {
            Name = model.Name, Created = model.Created,
            Events = model.Events.Where(a => a.Length >= 6)
                .Select(a => new MacroEvent(a[0] / 1000.0, (EventKind)a[1], (int)a[2], (int)a[3], (int)a[4], a[5] != 0,
                                            a.Length > 6 ? (int)a[6] : 0)).ToList(),
        };
    }
}
