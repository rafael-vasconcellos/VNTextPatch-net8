using System;

namespace VNTextPatch.Shared.Patching.Yuris
{
    /// <summary>
    /// Provides the 256-byte character range tables used by the YU-RIS runtime
    /// to identify the first byte of a multibyte character.
    /// A value of 1 marks a lead byte; 0 marks any other byte.
    /// </summary>
    public static class YurisCharacterRangeTable
    {
        public const int Length = 256;

        private static readonly byte[] ShiftJisBytes = Create(
            (0x81, 0x9F),
            (0xE0, 0xFC));

        private static readonly byte[] GbkBytes = Create(
            (0x81, 0xFE));

        /// <summary>
        /// The YU-RIS Shift-JIS lead-byte table.
        /// The returned span is read-only and has exactly 256 entries.
        /// </summary>
        public static ReadOnlySpan<byte> ShiftJis => ShiftJisBytes;

        /// <summary>
        /// The GBK lead-byte table used by the YU-RIS GBK patch.
        /// The returned span is read-only and has exactly 256 entries.
        /// </summary>
        public static ReadOnlySpan<byte> Gbk => GbkBytes;

        public static bool IsShiftJisLeadByte(byte value) =>
            ShiftJisBytes[value] != 0;

        public static bool IsGbkLeadByte(byte value) =>
            GbkBytes[value] != 0;

        private static byte[] Create(params (int Start, int End)[] ranges)
        {
            var table = new byte[Length];

            foreach ((int start, int end) in ranges)
            {
                if (start < 0 || end >= Length || start > end)
                    throw new ArgumentOutOfRangeException(nameof(ranges));

                Array.Fill(table, (byte)1, start, end - start + 1);
            }

            return table;
        }
    }
}
