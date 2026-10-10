namespace WindowsDynamicBar.Core.Settings;

/// <summary>Hotkey text ("Ctrl+Alt+V") &lt;-&gt; RegisterHotKey modifiers and virtual-key code.
/// Supported keys: A-Z, 0-9, F1-F24 and Space (Space needs a modifier).</summary>
public static class Hotkeys
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win" or "windows": mods |= ModWin; break;
                default: return false;
            }
        }

        var key = parts[^1];
        if (key.Equals("Space", StringComparison.OrdinalIgnoreCase))
        {
            if (mods == 0) return false;
            vk = 0x20;
            return true;
        }
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) { vk = c; return true; }
            return false;
        }
        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }
        return false;
    }

    /// <summary>Canonical text for a combination (Ctrl, Alt, Shift, Win order), or null if unsupported.</summary>
    public static string? Format(uint mods, uint vk)
    {
        string? key = vk switch
        {
            0x20 => "Space",
            >= 'A' and <= 'Z' => ((char)vk).ToString(),
            >= '0' and <= '9' => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
            _ => null,
        };
        if (key is null || (vk == 0x20 && mods == 0)) return null;
        var parts = new List<string>(5);
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModWin) != 0) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>Same combination regardless of spelling ("ctrl+alt+v" == "Alt+Ctrl+V").</summary>
    public static bool Same(string? a, string? b) =>
        TryParse(a, out uint ma, out uint va) && TryParse(b, out uint mb, out uint vb) && ma == mb && va == vb;

    /// <summary>Names of the app's other actions already using <paramref name="combo"/>.</summary>
    public static List<string> DuplicatesOf(SettingsModel s, string action, string combo)
    {
        var all = new (string Name, string Value)[]
        {
            ("Toggle island", s.HotkeyToggle), ("Media", s.HotkeyMedia), ("Timer", s.HotkeyTimer),
            ("Search", s.HotkeySearch), ("Clipboard", s.HotkeyClipboard),
        };
        return all.Where(h => h.Name != action && Same(h.Value, combo)).Select(h => h.Name).ToList();
    }
}
