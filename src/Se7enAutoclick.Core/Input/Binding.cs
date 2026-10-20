using System.Text.Json.Serialization;

namespace Se7enAutoclick.Core.Input;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

/// <summary>Un raccourci : une touche du clavier ou un bouton de souris, avec ou sans Ctrl / Maj / Alt / Windows.</summary>
public sealed record Binding(bool IsMouse, int Code, Mods Mods = Mods.None)
{
    public static Binding Key(int vk, Mods mods = Mods.None) => new(false, vk, mods);
    public static Binding Mouse(MouseButton b, Mods mods = Mods.None) => new(true, (int)b, mods);

    [JsonIgnore]
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
            if (Mods.HasFlag(Mods.Shift)) parts.Add("Maj");
            if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
            if (Mods.HasFlag(Mods.Win)) parts.Add("Win");
            parts.Add(IsMouse ? MouseName((MouseButton)Code) : KeyNames.Of(Code));
            return string.Join(" + ", parts);
        }
    }

    public static string MouseName(MouseButton b) => b switch
    {
        MouseButton.Left => "Clic gauche",
        MouseButton.Right => "Clic droit",
        MouseButton.Middle => "Clic molette",
        MouseButton.X1 => "Souris bouton 4",
        MouseButton.X2 => "Souris bouton 5",
        _ => "?",
    };

    public override string ToString() => Display;
}

/// <summary>Noms lisibles des touches (clavier français).</summary>
public static class KeyNames
{
    public static string Of(int vk) => vk switch
    {
        0x08 => "Retour arrière",
        0x09 => "Tab",
        0x0D => "Entrée",
        0x10 or 0xA0 or 0xA1 => "Maj",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 => "Alt",
        0xA5 => "Alt Gr",
        0x13 => "Pause",
        0x14 => "Verr. Maj",
        0x1B => "Échap",
        0x20 => "Espace",
        0x21 => "Page préc.",
        0x22 => "Page suiv.",
        0x23 => "Fin",
        0x24 => "Début",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2C => "Impr. écran",
        0x2D => "Inser",
        0x2E => "Suppr",
        0x5B or 0x5C => "Windows",
        0x5D => "Menu",
        0x6A => "Pavé *",
        0x6B => "Pavé +",
        0x6D => "Pavé -",
        0x6E => "Pavé .",
        0x6F => "Pavé /",
        0x90 => "Verr. Num",
        0x91 => "Arrêt défil.",
        0xBA => "$",
        0xBB => "=",
        0xBC => ",",
        0xBE => ";",
        0xBF => ":",
        0xC0 => "ù",
        0xDB => ")",
        0xDC => "*",
        0xDD => "^",
        0xDE => "²",
        0xDF => "!",
        0xE2 => "<",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Pavé " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => $"Touche {vk:X2}",
    };

    /// <summary>Touches de modification (jamais un raccourci à elles seules).</summary>
    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
}
