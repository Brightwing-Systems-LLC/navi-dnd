using System.Runtime.InteropServices;

namespace NaviDnD.UiTests;

// Reads the real console buffer via the Windows Console API — so the test sees
// exactly what the player would see, not what the program thinks it wrote.
internal static class ConsoleBufferReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(
        IntPtr hConsoleOutput, [Out] char[] lpCharacter, uint nLength,
        COORD dwReadCoord, out uint lpNumberOfCharsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleOutputAttribute(
        IntPtr hConsoleOutput, [Out] ushort[] lpAttribute, uint nLength,
        COORD dwReadCoord, out uint lpNumberOfAttrsRead);

    // Reads `length` characters from row `row` of the console buffer, starting at column 0.
    public static string ReadRow(int row, int length)
    {
        using var handle = ConsoleHandles.OpenOutput();
        var buffer = new char[length];
        var coord = new COORD { X = 0, Y = (short)row };

        if (!ReadConsoleOutputCharacterW(handle.DangerousGetHandle(), buffer, (uint)length, coord, out uint read))
            throw new InvalidOperationException("Failed to read the console buffer.");

        return new string(buffer, 0, (int)read);
    }

    // Reads the color attributes of a row (the console's legacy 16-color palette). The game paints
    // text with 24-bit ANSI — the legacy attributes may not reflect the exact RGB, but the difference
    // between "colored" and "not colored" (or between different colorings) can be detected.
    public static ushort[] ReadRowAttributes(int row, int length)
    {
        using var handle = ConsoleHandles.OpenOutput();
        var buffer = new ushort[length];
        var coord = new COORD { X = 0, Y = (short)row };

        if (!ReadConsoleOutputAttribute(handle.DangerousGetHandle(), buffer, (uint)length, coord, out uint read))
            throw new InvalidOperationException("Failed to read the console buffer's color attributes.");

        return buffer;
    }
}
