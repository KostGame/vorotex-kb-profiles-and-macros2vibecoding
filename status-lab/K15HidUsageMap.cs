namespace Vorotex.K15.StatusLab;

internal static class K15HidUsageMap
{
    internal static bool TryVirtualKey(byte usage, out ushort virtualKey)
    {
        if (usage is >= 0x04 and <= 0x1D)
        {
            virtualKey = checked((ushort)('A' + usage - 0x04));
            return true;
        }

        if (usage is >= 0x1E and <= 0x26)
        {
            virtualKey = checked((ushort)('1' + usage - 0x1E));
            return true;
        }

        if (usage == 0x27)
        {
            virtualKey = '0';
            return true;
        }

        virtualKey = usage switch
        {
            0x28 => 0x0D, // VK_RETURN
            0x29 => 0x1B, // VK_ESCAPE
            0x2A => 0x08, // VK_BACK
            0x2B => 0x09, // VK_TAB
            0x2C => 0x20, // VK_SPACE
            0x2D => 0xBD, // VK_OEM_MINUS
            0x2E => 0xBB, // VK_OEM_PLUS
            0x2F => 0xDB, // VK_OEM_4
            0x30 => 0xDD, // VK_OEM_6
            0x31 => 0xDC, // VK_OEM_5
            0x33 => 0xBA, // VK_OEM_1
            0x34 => 0xDE, // VK_OEM_7
            0x35 => 0xC0, // VK_OEM_3
            0x36 => 0xBC, // VK_OEM_COMMA
            0x37 => 0xBE, // VK_OEM_PERIOD
            0x38 => 0xBF, // VK_OEM_2
            0xE0 => 0xA2, // VK_LCONTROL
            0xE1 => 0xA0, // VK_LSHIFT
            0xE2 => 0xA4, // VK_LMENU
            _ => 0
        };
        return virtualKey != 0;
    }
}
