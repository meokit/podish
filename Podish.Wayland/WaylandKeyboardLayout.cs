using System.Collections.ObjectModel;
using System.Text;

namespace Podish.Wayland;

public enum WaylandKeyboardModifierRole
{
    None,
    Shift,
    Control,
    Alt,
    Super,
    CapsLock,
    NumLock
}

public readonly record struct WaylandKeyboardKeyDescriptor(
    int SdlScancode,
    uint EvdevKey,
    string XkbKeyName,
    string Symbols,
    WaylandKeyboardModifierRole ModifierRole = WaylandKeyboardModifierRole.None);

public static class WaylandKeyboardLayout
{
    private static readonly ReadOnlyDictionary<int, WaylandKeyboardKeyDescriptor> BySdlScancode =
        new(CreateDescriptors().ToDictionary(static x => x.SdlScancode));

    private static readonly ReadOnlyDictionary<uint, WaylandKeyboardKeyDescriptor> ByEvdevKey =
        new(CreateDescriptors().ToDictionary(static x => x.EvdevKey));

    public static bool TryGetBySdlScancode(int sdlScancode, out WaylandKeyboardKeyDescriptor descriptor)
    {
        return BySdlScancode.TryGetValue(sdlScancode, out descriptor);
    }

    public static bool TryGetByEvdevKey(uint evdevKey, out WaylandKeyboardKeyDescriptor descriptor)
    {
        return ByEvdevKey.TryGetValue(evdevKey, out descriptor);
    }

    public static string GenerateXkbKeymap()
    {
        var sb = new StringBuilder();
        sb.AppendLine("xkb_keymap {");
        sb.AppendLine("xkb_keycodes \"podish\" {");
        sb.AppendLine("    minimum = 8;");
        sb.AppendLine("    maximum = 255;");
        foreach (WaylandKeyboardKeyDescriptor descriptor in BySdlScancode.Values.OrderBy(static x => x.EvdevKey))
            sb.AppendLine($"    <{descriptor.XkbKeyName}> = {descriptor.EvdevKey + 8};");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("xkb_types \"podish\" {");
        sb.AppendLine("    include \"complete\"");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("xkb_compatibility \"podish\" {");
        sb.AppendLine("    include \"complete\"");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("xkb_symbols \"podish\" {");
        sb.AppendLine("    name[group1] = \"Podish US\";");
        foreach (WaylandKeyboardKeyDescriptor descriptor in BySdlScancode.Values.OrderBy(static x => x.EvdevKey))
            sb.AppendLine($"    key <{descriptor.XkbKeyName}> {{ [ {descriptor.Symbols} ] }};");
        sb.AppendLine("    modifier_map Shift { <LFSH>, <RTSH> };");
        sb.AppendLine("    modifier_map Lock { <CAPS> };");
        sb.AppendLine("    modifier_map Control { <LCTL>, <RCTL> };");
        sb.AppendLine("    modifier_map Mod1 { <LALT>, <RALT> };");
        sb.AppendLine("    modifier_map Mod2 { <NMLK> };");
        sb.AppendLine("    modifier_map Mod4 { <LWIN>, <RWIN> };");
        sb.AppendLine("};");
        sb.AppendLine("};");
        return sb.ToString();
    }

    private static WaylandKeyboardKeyDescriptor[] CreateDescriptors()
    {
        return
        [
            new(4, 30, "AC01", "a, A"),
            new(5, 48, "AB05", "b, B"),
            new(6, 46, "AB03", "c, C"),
            new(7, 32, "AC03", "d, D"),
            new(8, 18, "AD03", "e, E"),
            new(9, 33, "AC04", "f, F"),
            new(10, 34, "AC05", "g, G"),
            new(11, 35, "AC06", "h, H"),
            new(12, 23, "AD08", "i, I"),
            new(13, 36, "AC07", "j, J"),
            new(14, 37, "AC08", "k, K"),
            new(15, 38, "AC09", "l, L"),
            new(16, 50, "AB07", "m, M"),
            new(17, 49, "AB06", "n, N"),
            new(18, 24, "AD09", "o, O"),
            new(19, 25, "AD10", "p, P"),
            new(20, 16, "AD01", "q, Q"),
            new(21, 19, "AD04", "r, R"),
            new(22, 31, "AC02", "s, S"),
            new(23, 20, "AD05", "t, T"),
            new(24, 22, "AD07", "u, U"),
            new(25, 47, "AB04", "v, V"),
            new(26, 17, "AD02", "w, W"),
            new(27, 45, "AB02", "x, X"),
            new(28, 21, "AD06", "y, Y"),
            new(29, 44, "AB01", "z, Z"),
            new(30, 2, "AE01", "1, exclam"),
            new(31, 3, "AE02", "2, at"),
            new(32, 4, "AE03", "3, numbersign"),
            new(33, 5, "AE04", "4, dollar"),
            new(34, 6, "AE05", "5, percent"),
            new(35, 7, "AE06", "6, asciicircum"),
            new(36, 8, "AE07", "7, ampersand"),
            new(37, 9, "AE08", "8, asterisk"),
            new(38, 10, "AE09", "9, parenleft"),
            new(39, 11, "AE10", "0, parenright"),
            new(40, 28, "RTRN", "Return"),
            new(41, 1, "ESC", "Escape"),
            new(42, 14, "BKSP", "BackSpace"),
            new(43, 15, "TAB", "Tab, ISO_Left_Tab"),
            new(44, 57, "SPCE", "space"),
            new(45, 12, "AE11", "minus, underscore"),
            new(46, 13, "AE12", "equal, plus"),
            new(47, 26, "AD11", "bracketleft, braceleft"),
            new(48, 27, "AD12", "bracketright, braceright"),
            new(49, 43, "BKSL", "backslash, bar"),
            new(51, 39, "AC10", "semicolon, colon"),
            new(52, 40, "AC11", "apostrophe, quotedbl"),
            new(53, 41, "TLDE", "grave, asciitilde"),
            new(54, 51, "AB08", "comma, less"),
            new(55, 52, "AB09", "period, greater"),
            new(56, 53, "AB10", "slash, question"),
            new(57, 58, "CAPS", "Caps_Lock", WaylandKeyboardModifierRole.CapsLock),
            new(58, 59, "FK01", "F1"),
            new(59, 60, "FK02", "F2"),
            new(60, 61, "FK03", "F3"),
            new(61, 62, "FK04", "F4"),
            new(62, 63, "FK05", "F5"),
            new(63, 64, "FK06", "F6"),
            new(64, 65, "FK07", "F7"),
            new(65, 66, "FK08", "F8"),
            new(66, 67, "FK09", "F9"),
            new(67, 68, "FK10", "F10"),
            new(68, 87, "FK11", "F11"),
            new(69, 88, "FK12", "F12"),
            new(73, 110, "INS", "Insert"),
            new(74, 102, "HOME", "Home"),
            new(75, 104, "PGUP", "Prior"),
            new(76, 111, "DELE", "Delete"),
            new(77, 107, "END", "End"),
            new(78, 109, "PGDN", "Next"),
            new(79, 106, "RGHT", "Right"),
            new(80, 105, "LEFT", "Left"),
            new(81, 108, "DOWN", "Down"),
            new(82, 103, "UP", "Up"),
            new(83, 69, "NMLK", "Num_Lock", WaylandKeyboardModifierRole.NumLock),
            new(224, 29, "LCTL", "Control_L", WaylandKeyboardModifierRole.Control),
            new(225, 42, "LFSH", "Shift_L", WaylandKeyboardModifierRole.Shift),
            new(226, 56, "LALT", "Alt_L", WaylandKeyboardModifierRole.Alt),
            new(227, 125, "LWIN", "Super_L", WaylandKeyboardModifierRole.Super),
            new(228, 97, "RCTL", "Control_R", WaylandKeyboardModifierRole.Control),
            new(229, 54, "RTSH", "Shift_R", WaylandKeyboardModifierRole.Shift),
            new(230, 100, "RALT", "Alt_R", WaylandKeyboardModifierRole.Alt),
            new(231, 126, "RWIN", "Super_R", WaylandKeyboardModifierRole.Super)
        ];
    }
}
