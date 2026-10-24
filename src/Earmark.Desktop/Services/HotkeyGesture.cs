namespace Earmark.Desktop.Services;

public sealed record HotkeyGesture(bool Control, bool Alt, bool Shift, bool Win, uint VirtualKey)
{
    public const string Default = "Win+Alt+V";

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
    };

    public uint Modifiers => 0x4000u | (Alt ? 0x1u : 0) | (Control ? 0x2u : 0) | (Shift ? 0x4u : 0) | (Win ? 0x8u : 0);

    public override string ToString() =>
        string.Join('+', new[] { Control ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Win ? "Win" : null, KeyName(VirtualKey) }.OfType<string>());

    public static HotkeyGesture? Parse(string? text)
    {
        bool control = false, alt = false, shift = false, win = false;
        uint key = 0;
        foreach (var part in (text ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": control = true; break;
                case "ALT": alt = true; break;
                case "SHIFT": shift = true; break;
                case "WIN" or "WINDOWS": win = true; break;
                default:
                    if (key != 0 || VirtualKeyOf(part) is not (> 0 and var vk))
                    {
                        return null;
                    }

                    key = vk;
                    break;
            }
        }

        return key != 0 && (control || alt || win) ? new HotkeyGesture(control, alt, shift, win, key) : null;
    }

    public static string? KeyName(uint vk) => vk switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => NamedKeys.FirstOrDefault(pair => pair.Value == vk).Key,
    };

    private static uint VirtualKeyOf(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is var c and (>= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            return c;
        }

        if (name.Length > 1 && name[0] is 'F' or 'f' && int.TryParse(name[1..], out var function) && function is >= 1 and <= 24)
        {
            return (uint)(0x6F + function);
        }

        return NamedKeys.GetValueOrDefault(name.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : name);
    }
}
