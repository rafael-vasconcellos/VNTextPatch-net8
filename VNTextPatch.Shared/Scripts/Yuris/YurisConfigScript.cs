using System;
using Microsoft.Extensions.Logging;
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
    public enum YurisConfigLayout
    {
        V2,
        V5
    }

    /// <summary>
    /// Parser and structured editor for YU-RIS project configuration (YSCF,
    /// normally stored as yscfg.ybn).
    /// </summary>
    public sealed partial class YurisConfigScript : IScript, IStructuredScriptData
    {
        private static readonly ILogger Logger = AppLogging.Default;
        private const int V2CaptionLengthOffset = 0x40;
        private const int V2HeaderSize = V2CaptionLengthOffset + sizeof(ushort);
        private const int V5CaptionLengthOffset = 0x4C;
        private const int V5HeaderSize = V5CaptionLengthOffset + sizeof(ushort);
        private const uint V2VersionUpperBound = 466;
        private const int ImageTypeSlotCount = 8;
        private const int SoundTypeSlotCount = 4;
        private const string JsonFormat = "vntextpatch.yuris-config.v2";
        private const string LegacyJsonFormat = "vntextpatch.yuris-config.v1";

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
        private YurisEncoding _encoding = YurisEncoding.Default;
        private bool _hasCaption;
        private ushort _captionLengthField;
        private int _captionLengthOffset;
        private int _headerSize;

        public string Extension => ".ybn";

        public bool SupportsStructuredData => _data != null;

        public uint Version { get; private set; }

        public YurisConfigLayout Layout { get; private set; }

        /// <summary>
        /// False for header-only YSCF variants where the final ushort does not
        /// describe caption bytes. In that case the field is preserved verbatim.
        /// </summary>
        public bool HasCaption => _data != null && _hasCaption;

        public void Load(ScriptLocation location)
        {
            _encoding = YurisEncoding.FromLocation(location);
            byte[] data = File.ReadAllBytes(location.ToFilePath());
            Parse(data);
        }

        public void Parse(byte[] data)
        {
            _data = null;
            _hasCaption = false;
            _captionLengthField = 0;
            _captionLengthOffset = 0;
            _headerSize = 0;

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length < V2HeaderSize)
                throw new InvalidDataException("YSCF configuration header is truncated.");

            if (!data.AsSpan(0, 4).SequenceEqual("YSCF"u8))
                throw new InvalidDataException("Invalid YSCF signature.");

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));

            bool v2Valid = TryValidateLayout(
                data, V2CaptionLengthOffset, V2HeaderSize,
                out bool v2HasCaption, out ushort v2CaptionLength);

            bool v5Valid = TryValidateLayout(
                data, V5CaptionLengthOffset, V5HeaderSize,
                out bool v5HasCaption, out ushort v5CaptionLength);

            if (!v2Valid && !v5Valid)
            {
                throw new InvalidDataException(
                    $"YSCF version {version} does not match the known V2 or V5 layouts. " +
                    $"V2 expects a {V2HeaderSize}-byte header with caption length at 0x{V2CaptionLengthOffset:X}; " +
                    $"V5 expects a {V5HeaderSize}-byte header with caption length at 0x{V5CaptionLengthOffset:X}. " +
                    $"Actual file size: {data.Length} bytes.");
            }

            bool useV2;
            if (v2Valid && v5Valid)
            {
                // A V2 file with a 12-byte caption is exactly 78 bytes long,
                // which is also the fixed V5 header size. In that ambiguous
                // case the known legacy version range resolves the layout.
                useV2 = version <= V2VersionUpperBound;
            }
            else
            {
                useV2 = v2Valid;
            }

            Layout = useV2 ? YurisConfigLayout.V2 : YurisConfigLayout.V5;
            _captionLengthOffset = useV2 ? V2CaptionLengthOffset : V5CaptionLengthOffset;
            _headerSize = useV2 ? V2HeaderSize : V5HeaderSize;
            _hasCaption = useV2 ? v2HasCaption : v5HasCaption;
            _captionLengthField = useV2 ? v2CaptionLength : v5CaptionLength;

            int availableCaptionBytes = data.Length - _headerSize;
            if (_hasCaption &&
                availableCaptionBytes > _captionLengthField &&
                IsPrintableCaption(data.AsSpan(_headerSize, availableCaptionBytes)))
            {
                Logger.LogWarning(
                    $"[YU-RIS] Warning: YSCF version {version} declares a caption length of " +
                    $"{_captionLengthField} bytes, but its text payload contains " +
                    $"{availableCaptionBytes} bytes. Correcting the in-memory length.");

                _captionLengthField = checked((ushort)availableCaptionBytes);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    data.AsSpan(_captionLengthOffset, sizeof(ushort)),
                    _captionLengthField);
            }

            Version = version;
            _data = (byte[])data.Clone();
        }

        private static bool IsPrintableCaption(ReadOnlySpan<byte> bytes)
        {
            try
            {
                Encoding strictEncoding = (Encoding)StringUtil.SjisEncoding.Clone();
                strictEncoding.DecoderFallback = DecoderFallback.ExceptionFallback;
                string value = strictEncoding.GetString(bytes);

                return value.Length > 0 &&
                       value.All(character =>
                           !char.IsControl(character) && character != '\uFFFD');
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool TryValidateLayout(
            byte[] data,
            int captionLengthOffset,
            int headerSize,
            out bool hasCaption,
            out ushort captionLength)
        {
            hasCaption = false;
            captionLength = 0;

            if (data.Length < headerSize)
                return false;

            captionLength = BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(captionLengthOffset, sizeof(ushort)));

            if (data.Length == headerSize)
            {
                // Preserve header-only variants whose final word is reserved
                // or otherwise not a caption length. A zero value represents
                // a valid empty caption.
                hasCaption = captionLength == 0;
                return true;
            }

            if (data.Length < headerSize + captionLength)
                return false;

            if (data.Length > headerSize + captionLength &&
                (data.Length - headerSize > ushort.MaxValue ||
                 !IsPrintableCaption(data.AsSpan(headerSize))))
                return false;

            hasCaption = captionLength > 0;
            return true;
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            if (_data == null)
                yield break;

            if (_hasCaption)
                yield return new ScriptString(ReadCaption(_data), ScriptStringType.Message);
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            if (_data == null)
                throw new InvalidOperationException("YSCF has not been loaded.");

            List<ScriptString> entries = strings.ToList();
            if (!_hasCaption)
            {
                if (entries.Count != 0)
                    throw new InvalidDataException(
                        "This header-only YSCF variant has no safely editable caption field.");

                WriteOutput(location, (byte[])_data.Clone());
                return;
            }

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

            byte[] header = _data.AsSpan(0, _headerSize).ToArray();

            // Unknown/reserved fields are exported for inspection but are not
            // rewritten. All recognized settings below remain editable.
            EnsureUnchangedUInt32(root, "padding0", "unknown0", ReadUInt32(_data, 8));

            if (Layout == YurisConfigLayout.V5)
            {
                EnsureUnchangedUInt32(root, "padding1", "unknown1", ReadUInt32(_data, 72));
                PatchUInt32(root, header, "filePriorityDebug", 64);
                PatchUInt32(root, header, "filePriorityRelease", 68);
            }

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

            PatchByteArray(root, header, "imageTypeSlots", 28, ImageTypeSlotCount);
            PatchByteArray(root, header, "soundTypeSlots", 36, SoundTypeSlotCount);

            byte[] output;
            if (_hasCaption)
            {
                string caption = ReadOptionalString(root, "caption") ?? ReadCaption(_data);
                output = BuildPatchedData(header, caption);
            }
            else
            {
                // The 16-bit slot is not a valid caption length in this
                // header-only variant. Keep it byte-for-byte unchanged.
                JsonElement captionValue = root.TryGetProperty("caption", out JsonElement suppliedCaption)
                    ? suppliedCaption
                    : default;

                if (captionValue.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                    throw new InvalidDataException(
                        "Caption editing is unavailable for this header-only YSCF variant.");

                output = header;
            }

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
                Layout = Layout.ToString(),
                Padding0 = ReadUInt32(data, 8),
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
                FilePriorityDebug = Layout == YurisConfigLayout.V5 ? ReadUInt32(data, 64) : null,
                FilePriorityRelease = Layout == YurisConfigLayout.V5 ? ReadUInt32(data, 68) : null,
                Padding1 = Layout == YurisConfigLayout.V5 ? ReadUInt32(data, 72) : null,
                HasCaption = _hasCaption,
                CaptionLengthField = _captionLengthField,
                Caption = _hasCaption ? ReadCaption(data) : null
            };
        }

        private void ValidateIdentity(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("YSCF JSON root must be an object.");

            if (!root.TryGetProperty("format", out JsonElement format) ||
                format.ValueKind != JsonValueKind.String ||
                (format.GetString() != JsonFormat && format.GetString() != LegacyJsonFormat))
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

            // Older exports did not include this property, so absence remains
            // accepted for backward compatibility.
            if (root.TryGetProperty("layout", out JsonElement layout) &&
                (layout.ValueKind != JsonValueKind.String ||
                 !string.Equals(layout.GetString(), Layout.ToString(), StringComparison.Ordinal)))
                throw new InvalidDataException("YSCF JSON was exported from a different binary layout.");
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
            JsonElement root,
            string propertyName,
            string legacyPropertyName,
            uint originalValue)
        {
            // v1 exports called these fields unknown0/unknown1. v2 uses their
            // documented meaning from yuri.md: reserved padding words.
            EnsureUnchangedUInt32(root, propertyName, originalValue);
            EnsureUnchangedUInt32(root, legacyPropertyName, originalValue);
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

        private byte[] BuildPatchedData(byte[] header, string caption)
        {
            byte[] captionBytes = _encoding.GetWriteBytes(caption);
            if (captionBytes.Length > ushort.MaxValue)
                throw new InvalidDataException("YSCF caption exceeds the 16-bit length limit.");

            byte[] output = new byte[_headerSize + captionBytes.Length];
            Buffer.BlockCopy(header, 0, output, 0, _headerSize);
            BinaryPrimitives.WriteUInt16LittleEndian(
                output.AsSpan(_captionLengthOffset, sizeof(ushort)),
                (ushort)captionBytes.Length);

            Buffer.BlockCopy(captionBytes, 0, output, _headerSize, captionBytes.Length);
            return output;
        }

        private string ReadCaption(byte[] data)
        {
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(_captionLengthOffset, sizeof(ushort)));
            if (data.Length != _headerSize + length)
                throw new InvalidDataException("YSCF caption is unavailable for this layout.");

            return _encoding.ReadEncoding.GetString(data, _headerSize, length);
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

        private void WriteOutput(ScriptLocation destination, byte[] data)
        {
            string path = destination.ToFilePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(path, data);
            _encoding.EnsureExecutablePatched(destination);
        }

        private sealed class YurisConfigExport
        {
            public string Format { get; set; } = JsonFormat;
            public string Magic { get; set; } = "YSCF";
            public uint Version { get; set; }
            public string Layout { get; set; } = string.Empty;
            public uint Padding0 { get; set; }
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
            public uint? FilePriorityDebug { get; set; }
            public uint? FilePriorityRelease { get; set; }
            public uint? Padding1 { get; set; }
            public bool HasCaption { get; set; }
            public ushort CaptionLengthField { get; set; }
            public string? Caption { get; set; }
        }

        [JsonSourceGenerationOptions(
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        [JsonSerializable(typeof(YurisConfigExport))]
        private partial class YurisConfigJsonContext : JsonSerializerContext
        {
        }
    }
}
