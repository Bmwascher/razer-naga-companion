using System.Windows.Input;

namespace NagaBatteryTray.Ui;

/// <summary>WPF <see cref="Key"/> ↔ USB HID keyboard usage (HUT 1.5 ch. 10) for the remap MVP, plus the
/// left-side modifier bits and display formatting. Only keys in this table can be captured; anything
/// else is rejected at capture time.</summary>
public static class KeyToHidUsage
{
    public const byte ModCtrl = 0x01, ModShift = 0x02, ModAlt = 0x04, ModWin = 0x08;

    // Single source for both lookup directions (Key -> usage, usage -> display name).
    private static readonly (Key Key, byte Usage, string Name)[] Map = BuildMap();

    private static (Key, byte, string)[] BuildMap()
    {
        var list = new List<(Key, byte, string)>();
        for (int i = 0; i < 26; i++) list.Add((Key.A + i, (byte)(0x04 + i), ((char)('A' + i)).ToString()));
        for (int i = 0; i < 9; i++) list.Add((Key.D1 + i, (byte)(0x1e + i), ((char)('1' + i)).ToString()));
        list.Add((Key.D0, 0x27, "0"));
        for (int i = 0; i < 12; i++) list.Add((Key.F1 + i, (byte)(0x3a + i), $"F{1 + i}"));
        for (int i = 0; i < 12; i++) list.Add((Key.F13 + i, (byte)(0x68 + i), $"F{13 + i}"));
        list.AddRange(new (Key, byte, string)[]
        {
            (Key.Enter, 0x28, "Enter"), (Key.Escape, 0x29, "Esc"), (Key.Back, 0x2a, "Backspace"),
            (Key.Tab, 0x2b, "Tab"), (Key.Space, 0x2c, "Space"),
            (Key.OemMinus, 0x2d, "-"), (Key.OemPlus, 0x2e, "="),
            (Key.OemOpenBrackets, 0x2f, "["), (Key.OemCloseBrackets, 0x30, "]"),
            (Key.OemPipe, 0x31, "\\"), (Key.OemSemicolon, 0x33, ";"), (Key.OemQuotes, 0x34, "'"),
            (Key.OemTilde, 0x35, "`"), (Key.OemComma, 0x36, ","), (Key.OemPeriod, 0x37, "."),
            (Key.OemQuestion, 0x38, "/"),
            (Key.PrintScreen, 0x46, "PrtSc"), (Key.Scroll, 0x47, "ScrollLock"), (Key.Pause, 0x48, "Pause"),
            (Key.Insert, 0x49, "Insert"), (Key.Home, 0x4a, "Home"), (Key.PageUp, 0x4b, "PgUp"),
            (Key.Delete, 0x4c, "Delete"), (Key.End, 0x4d, "End"), (Key.PageDown, 0x4e, "PgDn"),
            (Key.Right, 0x4f, "Right"), (Key.Left, 0x50, "Left"), (Key.Down, 0x51, "Down"), (Key.Up, 0x52, "Up"),
        });
        return list.ToArray();
    }

    public static bool TryGetUsage(Key key, out byte usage)
    {
        foreach (var (k, u, _) in Map)
            if (k == key) { usage = u; return true; }
        usage = 0;
        return false;
    }

    public static byte ToModifierBits(ModifierKeys mods) => (byte)(
        ((mods & ModifierKeys.Control) != 0 ? ModCtrl : 0) |
        ((mods & ModifierKeys.Shift) != 0 ? ModShift : 0) |
        ((mods & ModifierKeys.Alt) != 0 ? ModAlt : 0) |
        ((mods & ModifierKeys.Windows) != 0 ? ModWin : 0));

    /// <summary>"Ctrl+Shift+F5"-style display text; an unmapped usage renders as hex.</summary>
    public static string Describe(byte modifiers, byte usage)
    {
        string name = $"0x{usage:x2}";
        foreach (var (_, u, n) in Map)
            if (u == usage) { name = n; break; }
        var parts = new List<string>(5);
        if ((modifiers & ModCtrl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(name);
        return string.Join("+", parts);
    }
}
