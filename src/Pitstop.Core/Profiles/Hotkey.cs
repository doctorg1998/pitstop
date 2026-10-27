namespace Pitstop.Core.Profiles;

[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 } // Win32 MOD_* values

/// <summary>A global hotkey such as "Ctrl+Alt+P". <see cref="VirtualKey"/> is the Win32 VK code.</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, uint VirtualKey, string Text)
{
    public static bool TryParse(string? text, out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false; // a bare key would hijack normal typing

        var mods = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var mod = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (mod == HotkeyModifiers.None || mods.HasFlag(mod)) return false;
            mods |= mod;
        }
        if (mods == HotkeyModifiers.Shift) return false; // Shift+letter is just a capital letter

        var key = parts[^1].ToUpperInvariant();
        uint? vk = key switch
        {
            [var c] when c is >= 'A' and <= 'Z' or >= '0' and <= '9' => c,
            ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => (uint)(0x6F + f),
            _ => null,
        };
        if (vk is null) return false;

        hotkey = new Hotkey(mods, vk.Value, string.Join("+", parts[..^1].Select(Capitalize).Append(key)));
        return true;
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
