using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Binary;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Parser and structured editor for YU-RIS project configuration (YSCF,
    /// normally stored as yscfg.ybn).
    /// </summary>
    public sealed partial class YurisConfigScript : IScript, IStructuredScriptData
    {
        private const int CaptionLengthOffset = 0x4C;
        private const int CaptionOffset = CaptionLengthOffset + sizeof(ushort);
        private const int HeaderSize = CaptionOffset;
        private const int ImageTypeSlotCount = 8;
        private const int SoundTypeSlotCount = 4;
        private const string JsonFormat = "vntextpatch.yuris-config.v1";

        private static readonly YurisConfigJsonContext JsonContext = new(
            new JsonSerializerOptions(YurisConfigJsonContext.Default.Options)
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        private static readonly YurisConfigJsonContext JsonLinesContext = new(
            new JsonSerializerOptions(YurisConfigJsonContext.Default.Options)
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        private byte[]? _data;

        public string Extension => ".ybn";

        public bool SupportsStructuredData => _data != null;

        public uint Version { get; private set; }

        public void Load(ScriptLocation location)
        {
            byte[] data = File.ReadAllBytes(location.ToFilePath());
            Parse(data);
        }

        public void Parse(byte[] data)
        {
            _data = null;

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length < HeaderSize)
                throw new InvalidDataException("YSCF configuration header is truncated.");

            if (!data.AsSpan(0, 4).SequenceEqual("YSCF"u8))
                throw new InvalidDataException("Invalid YSCF signature.");

            ushort captionLength = BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(CaptionLengthOffset, sizeof(ushort)));

            if (data.Length != HeaderSize + captionLength)
            {
                throw new InvalidDataException(
                    $"YSCF caption length is inconsistent: header says {captionLength} bytes, " +
                    $"but the file contains {data.Length - HeaderSize} bytes.");
            }

            Version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
            _data = (byte[])data.Clone();
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            if (_data == null)
                yield break;

            yield return new ScriptString(ReadCaption(_data), ScriptStringType.Message);
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            if (_data == null)
                throw new InvalidOperationException("YSCF has not been loaded.");

            List<ScriptString> entries = strings.ToList();
            if (entries.Count != 1 || entries[0].Type != ScriptStringType.Message)
                throw new InvalidDataException("Exactly one YSCF caption translation is expected.");

            byte[] output = BuildPatchedData(_data, entries[0].Text);
            WriteOutput(location, output);
        }

        public void ExportStructuredData(ScriptLocation destination)
        {
            if (_data == null)
                throw new InvalidOperationException("YSCF has not been loaded.");

            string path = destination.ToFilePath();
            string extension = Path.GetExtension(path);
            bool writeJsonLines = extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
            if (!writeJsonLines && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("YSCF structured export supports only .json and .jsonl.");

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            YurisConfigExport config = CreateExport();
            if (writeJsonLines)
            {
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(config, JsonLinesContext.YurisConfigExport) + "\n",
                    new UTF8Encoding(false));
            }
            else
            {
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(config, JsonContext.YurisConfigExport),
                    new UTF8Encoding(false));
            }
        }

        public void ImportStructuredData(ScriptLocation source, ScriptLocation destination)
        {
            if (_data == null)
                throw new InvalidOperationException("YSCF has not been loaded.");

            JsonElement root = ReadJsonRoot(source.ToFilePath());
            ValidateIdentity(root);

            byte[] header = _data.AsSpan(0, HeaderSize).ToArray();

            // Unknown/reserved fields are exported for inspection but are not
            // rewritten. All recognized settings below remain editable.
            EnsureUnchangedUInt32(root, "unknown0", ReadUInt32(_data, 8));
            EnsureUnchangedUInt32(root, "unknown1", ReadUInt32(_data, 72));

            PatchUInt32(root, header, "compile", 12);
            PatchUInt32(root, header, "screenWidth", 16);
            PatchUInt32(root, header, "screenHeight", 20);
            PatchUInt32(root, header, "enable", 24);
            PatchUInt32(root, header, "thread", 40);
            PatchUInt32(root, header, "debugMode", 44);
            PatchUInt32(root, header, "sound", 48);
            PatchUInt32(root, header, "windowResize", 52);
            PatchUInt32(root, header, "windowFrame", 56);
            PatchUInt32(root, header, "filePriorityDev", 60);
            PatchUInt32(root, header, "filePriorityDebug", 64);
            PatchUInt32(root, header, "filePriorityRelease", 68);

            PatchByteArray(root, header, "imageTypeSlots", 28, ImageTypeSlotCount);
            PatchByteArray(root, header, "soundTypeSlots", 36, SoundTypeSlotCount);

            string caption = ReadOptionalString(root, "caption") ?? ReadCaption(_data);
            byte[] output = BuildPatchedData(header, caption);
            WriteOutput(destination, output);
        }

        private YurisConfigExport CreateExport()
        {
            byte[] data = _data!;
            return new YurisConfigExport
            {
                Format = JsonFormat,
                Magic = "YSCF",
                Version = Version,
                Unknown0 = ReadUInt32(data, 8),
                Compile = ReadUInt32(data, 12),
                ScreenWidth = ReadUInt32(data, 16),
                ScreenHeight = ReadUInt32(data, 20),
                Enable = ReadUInt32(data, 24),
                ImageTypeSlots = ReadByteArray(data, 28, ImageTypeSlotCount),
                SoundTypeSlots = ReadByteArray(data, 36, SoundTypeSlotCount),
                Thread = ReadUInt32(data, 40),
                DebugMode = ReadUInt32(data, 44),
                Sound = ReadUInt32(data, 48),
                WindowResize = ReadUInt32(data, 52),
                WindowFrame = ReadUInt32(data, 56),
                FilePriorityDev = ReadUInt32(data, 60),
                FilePriorityDebug = ReadUInt32(data, 64),
                FilePriorityRelease = ReadUInt32(data, 68),
                Unknown1 = ReadUInt32(data, 72),
                Caption = ReadCaption(data)
            };
        }

        private void ValidateIdentity(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("YSCF JSON root must be an object.");

            if (!root.TryGetProperty("format", out JsonElement format) ||
                format.ValueKind != JsonValueKind.String ||
                format.GetString() != JsonFormat)
                throw new InvalidDataException("Unrecognized YSCF JSON format.");

            if (!root.TryGetProperty("magic", out JsonElement magic) ||
                magic.ValueKind != JsonValueKind.String ||
                magic.GetString() != "YSCF")
                throw new InvalidDataException("YSCF JSON has an invalid magic value.");

            if (!root.TryGetProperty("version", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetUInt32(out uint jsonVersion) ||
                jsonVersion != Version)
                throw new InvalidDataException("YSCF JSON was exported from a different source version.");
        }

        private static JsonElement ReadJsonRoot(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("YSCF JSON file was not found.", path);

            string extension = Path.GetExtension(path);
            if (extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                string[] lines = File.ReadLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();

                if (lines.Length != 1)
                    throw new InvalidDataException("YSCF JSONL must contain exactly one configuration record.");

                using JsonDocument document = JsonDocument.Parse(lines[0]);
                return document.RootElement.Clone();
            }

            if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("YSCF structured import supports only .json and .jsonl.");

            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
                return document.RootElement.Clone();
        }

        private static void PatchUInt32(
            JsonElement root, byte[] header, string propertyName, int offset)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return;

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint number))
                throw new InvalidDataException($"YSCF property '{propertyName}' must be an unsigned 32-bit integer.");

            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset, 4), number);
        }

        private static void EnsureUnchangedUInt32(
            JsonElement root, string propertyName, uint originalValue)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return;

            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetUInt32(out uint number) ||
                number != originalValue)
                throw new InvalidDataException(
                    $"YSCF property '{propertyName}' is reserved and cannot be changed.");
        }

        private static void PatchByteArray(
            JsonElement root, byte[] header, string propertyName, int offset, int expectedLength)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return;

            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != expectedLength)
                throw new InvalidDataException(
                    $"YSCF property '{propertyName}' must contain exactly {expectedLength} byte values.");

            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number ||
                    !item.TryGetInt32(out int number) ||
                    number < byte.MinValue || number > byte.MaxValue)
                    throw new InvalidDataException(
                        $"YSCF property '{propertyName}[{index}]' must be between 0 and 255.");

                header[offset + index] = (byte)number;
                index++;
            }
        }

        private static string? ReadOptionalString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return null;

            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"YSCF property '{propertyName}' must be a string.");

            return value.GetString() ?? string.Empty;
        }

        private static byte[] BuildPatchedData(byte[] header, string caption)
        {
            byte[] captionBytes = StringUtil.SjisTunnelEncoding.GetBytes(caption);
            if (captionBytes.Length > ushort.MaxValue)
                throw new InvalidDataException("YSCF caption exceeds the 16-bit length limit.");

            byte[] output = new byte[HeaderSize + captionBytes.Length];
            Buffer.BlockCopy(header, 0, output, 0, HeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(
                output.AsSpan(CaptionLengthOffset, sizeof(ushort)),
                (ushort)captionBytes.Length);

            Buffer.BlockCopy(captionBytes, 0, output, HeaderSize, captionBytes.Length);
            return output;
        }

        private static string ReadCaption(byte[] data)
        {
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(CaptionLengthOffset, sizeof(ushort)));
            return StringUtil.SjisEncoding.GetString(data, HeaderSize, length);
        }

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));

        private static int[] ReadByteArray(byte[] data, int offset, int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = data[offset + i];

            return result;
        }

        private static void WriteOutput(ScriptLocation destination, byte[] data)
        {
            string path = destination.ToFilePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(path, data);
        }

        private sealed class YurisConfigExport
        {
            public string Format { get; set; } = JsonFormat;
            public string Magic { get; set; } = "YSCF";
            public uint Version { get; set; }
            public uint Unknown0 { get; set; }
            public uint Compile { get; set; }
            public uint ScreenWidth { get; set; }
            public uint ScreenHeight { get; set; }
            public uint Enable { get; set; }
            public int[] ImageTypeSlots { get; set; } = Array.Empty<int>();
            public int[] SoundTypeSlots { get; set; } = Array.Empty<int>();
            public uint Thread { get; set; }
            public uint DebugMode { get; set; }
            public uint Sound { get; set; }
            public uint WindowResize { get; set; }
            public uint WindowFrame { get; set; }
            public uint FilePriorityDev { get; set; }
            public uint FilePriorityDebug { get; set; }
            public uint FilePriorityRelease { get; set; }
            public uint Unknown1 { get; set; }
            public string Caption { get; set; } = string.Empty;
        }

        [JsonSourceGenerationOptions(
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        [JsonSerializable(typeof(YurisConfigExport))]
        private partial class YurisConfigJsonContext : JsonSerializerContext
        {
        }
    }
}
