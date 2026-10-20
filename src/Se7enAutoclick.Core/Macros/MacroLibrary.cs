namespace Se7enAutoclick.Core.Macros;

/// <summary>Les macros de l'utilisateur : un fichier .se7macro par macro, dans %AppData%\Se7enAutoclick\Macros.</summary>
public sealed class MacroLibrary(string folder)
{
    public string Folder { get; } = folder;

    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*" + Macro.Extension)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
    }

    public string PathOf(string name) => Path.Combine(Folder, SafeName(name) + Macro.Extension);

    public Macro Load(string name) => Macro.Load(PathOf(name));

    public void Save(Macro macro)
    {
        Directory.CreateDirectory(Folder);
        macro.Name = SafeName(macro.Name);
        macro.Save(PathOf(macro.Name));
    }

    /// <summary>Nom libre (« Macro 3 ») pour un nouvel enregistrement.</summary>
    public string NextName(string prefix = "Macro")
    {
        var names = new HashSet<string>(Names(), StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
            if (!names.Contains($"{prefix} {i}")) return $"{prefix} {i}";
    }

    public void Rename(string from, string to)
    {
        to = SafeName(to);
        if (string.Equals(from, to, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(to)) return;
        if (File.Exists(PathOf(to))) throw new IOException($"Une macro « {to} » existe déjà.");
        var m = Load(from);
        m.Name = to;
        m.Save(PathOf(to));
        File.Delete(PathOf(from));
    }

    public string Duplicate(string name)
    {
        var m = Load(name);
        m.Name = NextName(SafeName(name) + " copie");
        Save(m);
        return m.Name;
    }

    public void Delete(string name) => File.Delete(PathOf(name));

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim('.', ' ');
        return string.IsNullOrEmpty(clean) ? "Macro" : clean[..Math.Min(60, clean.Length)];
    }
}
