namespace Glide.Core.Settings;

public static class HotkeyModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
}

/// <summary>
/// Turns the settings form (<c>["Ctrl", "Shift"]</c> plus <c>"/"</c>) into the
/// modifier flags and virtual-key code that <c>RegisterHotKey</c> expects.
/// The OEM keys are layout dependent, which is why the key is stored as the
/// character the user types rather than as a raw code.
/// </summary>
public static class HotkeyParser
{
    public static bool TryParse(HotkeySettings settings, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        foreach (string modifier in settings.Modifiers)
        {
            uint bit = modifier.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => 0
            };

            if (bit == 0)
            {
                return false;
            }

            modifiers |= bit;
        }

        if (!TryParseKey(settings.Key, out virtualKey))
        {
            return false;
        }

        bool functionKey = virtualKey is >= 0x70 and <= 0x7B;

        return modifiers != 0 || functionKey;
    }

    public static bool TryParseKey(string key, out uint virtualKey)
    {
        virtualKey = 0;

        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            virtualKey = char.ToUpperInvariant(key[0]);
            return true;
        }

        virtualKey = key.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "ESCAPE" or "ESC" => 0x1B,
            "TAB" => 0x09,
            "BACKSPACE" or "BACK" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            ";" or "OEM_1" => 0xBA,
            "=" or "OEM_PLUS" => 0xBB,
            "," or "OEM_COMMA" => 0xBC,
            "-" or "OEM_MINUS" => 0xBD,
            "." or "OEM_PERIOD" => 0xBE,
            "/" or "?" or "OEM_2" => 0xBF,
            "`" or "~" or "OEM_3" => 0xC0,
            "[" or "OEM_4" => 0xDB,
            "\\" or "OEM_5" => 0xDC,
            "]" or "OEM_6" => 0xDD,
            "'" or "OEM_7" => 0xDE,
            _ => 0
        };

        return virtualKey != 0;
    }

    /// <summary>Reads <c>"Ctrl+Shift+Alt+/"</c> from the command line.</summary>
    public static bool TryParseSpec(string spec, out HotkeySettings settings)
    {
        settings = new HotkeySettings();
        string[] parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        settings = new HotkeySettings
        {
            Modifiers = parts[..^1],
            Key = parts[^1]
        };

        return true;
    }

    public static string ToSpec(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(5);

        if ((modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & HotkeyModifiers.Win) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(virtualKey switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Escape",
            0x09 => "Tab",
            0xBF => "/",
            >= 0x70 and <= 0x7B => $"F{virtualKey - 0x6F}",
            _ => ((char)virtualKey).ToString()
        });

        return string.Join("+", parts);
    }
}
