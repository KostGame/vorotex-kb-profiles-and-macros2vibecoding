namespace Vorotex.K15.Hud;

internal readonly record struct HotkeyBinding(uint Modifiers, Keys Key, string Display)
{
    public static HotkeyBinding ParseOrDefault(string? value, string fallback)
    {
        if (TryParse(value, out var parsed))
            return parsed;
        if (TryParse(fallback, out parsed))
            return parsed;
        throw new InvalidOperationException($"Invalid built-in hotkey: {fallback}");
    }

    public static bool TryParse(string? value, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        uint modifiers = 0;
        Keys key = Keys.None;

        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.ModControl;
                    continue;
                case "alt":
                    modifiers |= NativeMethods.ModAlt;
                    continue;
                case "shift":
                    modifiers |= NativeMethods.ModShift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.ModWin;
                    continue;
            }

            if (key != Keys.None || !Enum.TryParse(part, true, out key) || key == Keys.None)
                return false;
        }

        if (key == Keys.None)
            return false;

        binding = new HotkeyBinding(modifiers | NativeMethods.ModNoRepeat, key, NormalizeDisplay(modifiers, key));
        return true;
    }

    private static string NormalizeDisplay(uint modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");
        parts.Add(key.ToString().ToUpperInvariant());
        return string.Join('+', parts);
    }
}
