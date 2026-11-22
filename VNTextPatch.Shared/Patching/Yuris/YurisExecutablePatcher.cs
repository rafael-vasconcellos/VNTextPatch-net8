using System;
using System.Buffers.Binary;
using System.IO;

namespace VNTextPatch.Shared.Patching.Yuris
{
    public enum YurisExecutablePatchStatus
    {
        Patched,
        AlreadyPatched
    }

    public readonly record struct YurisExecutablePatchResult(
        YurisExecutablePatchStatus Status,
        long TableOffset,
        string InputPath,
        string OutputPath);

    /// <summary>
    /// Replaces the YU-RIS Shift-JIS lead-byte classification table in a
    /// Windows PE executable with the corresponding GBK table.
    /// The input executable is never modified; the patched copy is written
    /// to the requested output path.
    /// </summary>
    public static class YurisExecutablePatcher
    {
        public static YurisExecutablePatchResult Patch(
            string inputPath,
            string outputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
                throw new ArgumentException("An input executable path is required.", nameof(inputPath));

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("An output executable path is required.", nameof(outputPath));

            string sourcePath = Path.GetFullPath(inputPath);
            string destinationPath = Path.GetFullPath(outputPath);

            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (sourcePath.Equals(destinationPath, pathComparison))
                throw new ArgumentException(
                    "The output path must be different from the input path. The original executable is never modified.",
                    nameof(outputPath));

            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The input executable was not found.", sourcePath);

            byte[] data = File.ReadAllBytes(sourcePath);
            ValidatePortableExecutable(data, sourcePath);

            ReadOnlySpan<byte> shiftJisTable = YurisCharacterRangeTable.ShiftJis;
            ReadOnlySpan<byte> gbkTable = YurisCharacterRangeTable.Gbk;

            int shiftJisOffset = FindUnique(data, shiftJisTable, "Shift-JIS");
            int gbkOffset = FindUnique(data, gbkTable, "GBK");

            YurisExecutablePatchStatus status;
            int tableOffset;

            if (shiftJisOffset >= 0 && gbkOffset < 0)
            {
                gbkTable.CopyTo(data.AsSpan(shiftJisOffset, YurisCharacterRangeTable.Length));
                status = YurisExecutablePatchStatus.Patched;
                tableOffset = shiftJisOffset;
            }
            else if (shiftJisOffset < 0 && gbkOffset >= 0)
            {
                status = YurisExecutablePatchStatus.AlreadyPatched;
                tableOffset = gbkOffset;
            }
            else if (shiftJisOffset >= 0 && gbkOffset >= 0)
            {
                throw new InvalidDataException(
                    "The executable contains both the Shift-JIS and GBK range tables. " +
                    "It may be a hybrid or unsupported executable; no output was written.");
            }
            else
            {
                throw new NotSupportedException(
                    "The YU-RIS Shift-JIS character range table was not found. " +
                    "This executable may use an unsupported engine version or a different table layout.");
            }

            WriteAtomically(destinationPath, data);

            return new YurisExecutablePatchResult(
                status,
                tableOffset,
                sourcePath,
                destinationPath);
        }

        private static int FindUnique(byte[] data, ReadOnlySpan<byte> pattern, string tableName)
        {
            int matchOffset = -1;
            int searchOffset = 0;

            while (searchOffset <= data.Length - pattern.Length)
            {
                int relativeOffset = data.AsSpan(searchOffset).IndexOf(pattern);
                if (relativeOffset < 0)
                    break;

                int currentOffset = searchOffset + relativeOffset;
                if (matchOffset >= 0)
                {
                    throw new InvalidDataException(
                        $"The executable contains more than one {tableName} range table. " +
                        "The target is ambiguous; no output was written.");
                }

                matchOffset = currentOffset;
                searchOffset = currentOffset + 1;
            }

            return matchOffset;
        }

        private static void ValidatePortableExecutable(byte[] data, string path)
        {
            // DOS header: "MZ" and e_lfanew at offset 0x3C.
            if (data.Length < 0x40 ||
                data[0] != (byte)'M' ||
                data[1] != (byte)'Z')
            {
                throw new InvalidDataException(
                    $"'{path}' is not a valid Windows executable (missing MZ header).");
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, 4));
            if (peOffset < 0 || peOffset > data.Length - 4)
            {
                throw new InvalidDataException(
                    $"'{path}' has an invalid PE header offset.");
            }

            // PE signature: "PE\0\0".
            if (data[peOffset] != (byte)'P' ||
                data[peOffset + 1] != (byte)'E' ||
                data[peOffset + 2] != 0 ||
                data[peOffset + 3] != 0)
            {
                throw new InvalidDataException(
                    $"'{path}' does not contain a valid PE signature.");
            }
        }

        private static void WriteAtomically(string outputPath, byte[] data)
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllBytes(temporaryPath, data);
                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
