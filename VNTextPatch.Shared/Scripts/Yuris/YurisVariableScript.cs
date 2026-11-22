using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    public enum YurisVariableRecordLayout
    {
        WithoutFlag,
        WithFlag
    }

    public enum YurisVariableScope : byte
    {
        Global = 1,
        Local = 2,
        Static = 3
    }

    public enum YurisVariableValueType : byte
    {
        Uninitialized = 0,
        Integer = 1,
        Double = 2,
        StringExpression = 3
    }

    /// <summary>
    /// One variable declaration from ysv.ybn.
    /// </summary>
    public sealed class YurisVariableDeclaration
    {
        public byte Scope { get; }
        public byte Flag { get; }
        public ushort ScriptId { get; }
        public ushort Index { get; }
        public YurisVariableValueType ValueType { get; }
        public IReadOnlyList<uint> Dimensions { get; }

        public long? IntegerValue { get; }
        public double? DoubleValue { get; }

        /// <summary>
        /// Raw expression bytecode for string initializers.
        /// </summary>
        public byte[]? StringExpression { get; }

        /// <summary>
        /// Evaluated value when the expression is statically resolvable.
        /// Dynamic expressions retain a null value and their raw bytecode.
        /// </summary>
        public string? EvaluatedStringValue { get; }

        public bool IsArray => Dimensions.Count > 0;

        /// <summary>
        /// Stable key suitable for JSON export/import. It does not depend on
        /// display names, which are not stored in YSVR.
        /// </summary>
        public string Id => $"{Scope}:{Flag}:{ScriptId}:{Index}";

        internal int ValueOffset { get; }
        internal int ValueLength { get; }
        internal int StringLengthOffset { get; }

        internal YurisVariableDeclaration(
            byte scope,
            byte flag,
            ushort scriptId,
            ushort index,
            YurisVariableValueType valueType,
            IReadOnlyList<uint> dimensions,
            long? integerValue,
            double? doubleValue,
            byte[]? stringExpression,
            string? evaluatedStringValue,
            int valueOffset,
            int valueLength,
            int stringLengthOffset)
        {
            Scope = scope;
            Flag = flag;
            ScriptId = scriptId;
            Index = index;
            ValueType = valueType;
            Dimensions = dimensions;
            IntegerValue = integerValue;
            DoubleValue = doubleValue;
            StringExpression = stringExpression;
            EvaluatedStringValue = evaluatedStringValue;
            ValueOffset = valueOffset;
            ValueLength = valueLength;
            StringLengthOffset = stringLengthOffset;
        }
    }

    /// <summary>
    /// Parser and editable structured-data adapter for YU-RIS variable
    /// initialization data (ysv.ybn, magic YSVR).
    ///
    /// Exported variables use stable IDs and typed JSON values. Only initialized
    /// values are editable; unknown/dynamic expression bytecode is retained and
    /// exported as Base64 so it can be inspected without being destroyed.
    /// </summary>
    public sealed partial class YurisVariableScript : IScript, IStructuredScriptData
    {
        private const int HeaderSize = 10;
        private const int MaxDimensions = 32;
        private const string JsonFormat = "vntextpatch.yuris-variables.v1";

        private static readonly VariableJsonContext JsonContext = new(
            new JsonSerializerOptions(VariableJsonContext.Default.Options)
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        private static readonly VariableJsonContext JsonLinesContext = new(
            new JsonSerializerOptions(VariableJsonContext.Default.Options)
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        private byte[] _originalData = Array.Empty<byte>();
        private YurisEncoding _encoding = YurisEncoding.Default;

        public string Extension => ".ybn";

        public uint Version { get; private set; }

        public YurisVariableRecordLayout Layout { get; private set; }

        public IReadOnlyList<YurisVariableDeclaration> Variables { get; private set; }
            = Array.Empty<YurisVariableDeclaration>();

        public bool SupportsStructuredData => _originalData.Length >= HeaderSize;

        public IEnumerable<YurisVariableDeclaration> GlobalVariables =>
            Variables.Where(variable => variable.Scope == (byte)YurisVariableScope.Global);

        public IEnumerable<YurisVariableDeclaration> StaticVariables =>
            Variables.Where(variable => variable.Scope == (byte)YurisVariableScope.Static);

        public IEnumerable<YurisVariableDeclaration> LocalVariables(ushort scriptId) =>
            Variables.Where(variable =>
                variable.Scope == (byte)YurisVariableScope.Local &&
                variable.ScriptId == scriptId);

        public void Load(ScriptLocation location)
        {
            _encoding = YurisEncoding.FromLocation(location);
            Parse(File.ReadAllBytes(location.ToFilePath()));
        }

        /// <summary>
        /// Parse a complete YSVR file from memory.
        /// </summary>
        public void Parse(byte[] data)
        {
            _originalData = Array.Empty<byte>();
            Variables = Array.Empty<YurisVariableDeclaration>();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length < HeaderSize)
                throw new InvalidDataException("YSVR header is truncated.");

            if (!data.AsSpan(0, 4).SequenceEqual("YSVR"u8))
                throw new InvalidDataException("Invalid YSVR signature.");

            uint version = ReadUInt32(data, 4);
            int variableCount = ReadUInt16(data, 8);

            bool withFlagValid = TryParseRecords(
                data, variableCount, hasFlag: true,
                out List<YurisVariableDeclaration>? withFlag,
                out _,
                out string? withFlagError);

            bool withoutFlagValid = TryParseRecords(
                data, variableCount, hasFlag: false,
                out List<YurisVariableDeclaration>? withoutFlag,
                out _,
                out string? withoutFlagError);

            if (!withFlagValid && !withoutFlagValid)
            {
                throw new InvalidDataException(
                    $"Could not parse YSVR version {version}. " +
                    $"WithFlag: {withFlagError ?? "invalid layout"}. " +
                    $"WithoutFlag: {withoutFlagError ?? "invalid layout"}.");
            }

            bool useFlagLayout;
            if (withFlagValid && withoutFlagValid)
            {
                // Known samples: version 474 uses the 7-byte header;
                // versions 495 and 555 use the 8-byte header.
                useFlagLayout = version >= 495;
            }
            else
            {
                useFlagLayout = withFlagValid;
            }

            Version = version;
            Layout = useFlagLayout
                ? YurisVariableRecordLayout.WithFlag
                : YurisVariableRecordLayout.WithoutFlag;
            Variables = (useFlagLayout ? withFlag! : withoutFlag!).AsReadOnly();
            _originalData = (byte[])data.Clone();
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            // Variables are editable metadata, not dialogue strings.
            yield break;
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            using IEnumerator<ScriptString> enumerator = strings.GetEnumerator();
            if (enumerator.MoveNext())
                throw new InvalidDataException("YSVR contains no translatable strings.");

            if (_originalData.Length == 0)
                throw new InvalidOperationException("YSVR has not been loaded.");

            File.WriteAllBytes(location.ToFilePath(), _originalData);
        }

        public void ExportStructuredData(ScriptLocation destination)
        {
            if (!SupportsStructuredData)
                throw new InvalidOperationException("YSVR has not been loaded.");

            string path = destination.ToFilePath();
            string extension = Path.GetExtension(path);
            bool writeJsonLines = extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
            if (!writeJsonLines && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("YSVR structured export supports only .json and .jsonl.");

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var document = new VariableExportDocument
            {
                Format = JsonFormat,
                Version = Version,
                Layout = Layout.ToString(),
                Variables = Variables.Select(ToExportRecord).ToList()
            };

            if (writeJsonLines)
            {
                using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
                foreach (VariableExportRecord variable in document.Variables)
                    writer.WriteLine(JsonSerializer.Serialize(
                        variable, JsonLinesContext.VariableExportRecord));
            }
            else
            {
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(document, JsonContext.VariableExportDocument),
                    new UTF8Encoding(false));
            }
        }

        public void ImportStructuredData(ScriptLocation source, ScriptLocation destination)
        {
            if (!SupportsStructuredData)
                throw new InvalidOperationException("YSVR has not been loaded.");

            Dictionary<string, JsonElement> editedValues = ReadEditedValues(
                source.ToFilePath(), Version, Layout.ToString());
            var variablesById = Variables.ToDictionary(variable => variable.Id, StringComparer.Ordinal);

            foreach (string id in editedValues.Keys)
            {
                if (!variablesById.ContainsKey(id))
                    throw new InvalidDataException($"JSON contains a variable ID not present in this YSVR file: {id}");
            }

            var patches = new List<BinaryPatch>();

            foreach (YurisVariableDeclaration variable in Variables)
            {
                if (!editedValues.TryGetValue(variable.Id, out JsonElement record))
                    continue;

                if (!record.TryGetProperty("value", out JsonElement value))
                    continue;

                if (value.ValueKind == JsonValueKind.Null)
                    continue;

                switch (variable.ValueType)
                {
                    case YurisVariableValueType.Uninitialized:
                        throw new InvalidDataException(
                            $"Variable {variable.Id} has no initializer and cannot be assigned through YSVR.");

                    case YurisVariableValueType.Integer:
                    {
                        if (value.ValueKind != JsonValueKind.Number ||
                            !value.TryGetInt64(out long integerValue))
                            throw new InvalidDataException(
                                $"Variable {variable.Id} requires an Int64 JSON value.");

                        if (variable.IntegerValue == integerValue)
                            break;

                        byte[] replacement = new byte[8];
                        BinaryPrimitives.WriteInt64LittleEndian(replacement, integerValue);
                        patches.Add(new BinaryPatch(variable.ValueOffset, 8, replacement));
                        break;
                    }

                    case YurisVariableValueType.Double:
                    {
                        if (value.ValueKind != JsonValueKind.Number ||
                            !value.TryGetDouble(out double doubleValue) ||
                            double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                            throw new InvalidDataException(
                                $"Variable {variable.Id} requires a finite numeric JSON value.");

                        if (variable.DoubleValue.HasValue &&
                            BitConverter.DoubleToInt64Bits(variable.DoubleValue.Value) ==
                            BitConverter.DoubleToInt64Bits(doubleValue))
                            break;

                        byte[] replacement = new byte[8];
                        BinaryPrimitives.WriteInt64LittleEndian(
                            replacement, BitConverter.DoubleToInt64Bits(doubleValue));
                        patches.Add(new BinaryPatch(variable.ValueOffset, 8, replacement));
                        break;
                    }

                    case YurisVariableValueType.StringExpression:
                    {
                        if (value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException(
                                $"Variable {variable.Id} requires a string JSON value.");

                        string stringValue = value.GetString() ?? string.Empty;
                        if (variable.EvaluatedStringValue == stringValue)
                            break;

                        byte[] expression = SerializeStringExpression(stringValue);
                        if (expression.Length > ushort.MaxValue)
                            throw new InvalidDataException(
                                $"Variable {variable.Id} expression exceeds the YSVR 16-bit length limit.");

                        byte[] replacement = new byte[2 + expression.Length];
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            replacement.AsSpan(0, 2), (ushort)expression.Length);
                        Buffer.BlockCopy(expression, 0, replacement, 2, expression.Length);

                        patches.Add(new BinaryPatch(
                            variable.StringLengthOffset,
                            2 + variable.ValueLength,
                            replacement));
                        break;
                    }

                    default:
                        throw new InvalidDataException(
                            $"Variable {variable.Id} has unsupported value type {(byte)variable.ValueType}.");
                }
            }

            byte[] output = ApplyPatches(_originalData, patches);
            string destinationPath = destination.ToFilePath();
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            File.WriteAllBytes(destinationPath, output);
        }

        private VariableExportRecord ToExportRecord(YurisVariableDeclaration variable)
        {
            JsonElement? value = variable.ValueType switch
            {
                YurisVariableValueType.Integer when variable.IntegerValue.HasValue =>
                    SerializeValue(variable.IntegerValue.Value),
                YurisVariableValueType.Double when
                    variable.DoubleValue.HasValue &&
                    !double.IsNaN(variable.DoubleValue.Value) &&
                    !double.IsInfinity(variable.DoubleValue.Value) =>
                    SerializeValue(variable.DoubleValue.Value),
                YurisVariableValueType.StringExpression when
                    variable.EvaluatedStringValue != null =>
                    SerializeValue(variable.EvaluatedStringValue),
                _ => null
            };

            return new VariableExportRecord
            {
                Format = JsonFormat,
                Version = Version,
                Layout = Layout.ToString(),
                Id = variable.Id,
                Scope = GetScopeName(variable.Scope),
                ScopeValue = variable.Scope,
                Flag = variable.Flag,
                ScriptId = variable.ScriptId,
                Index = variable.Index,
                Type = GetTypeName(variable.ValueType),
                TypeValue = (byte)variable.ValueType,
                Dimensions = variable.Dimensions.ToArray(),
                Value = value,
                IsEvaluated = variable.ValueType != YurisVariableValueType.StringExpression ||
                              variable.EvaluatedStringValue != null,
                ExpressionBase64 = variable.StringExpression == null
                    ? null
                    : Convert.ToBase64String(variable.StringExpression)
            };
        }

        private static JsonElement SerializeValue<T>(T value)
        {
            JsonTypeInfo<T> typeInfo =
                (JsonTypeInfo<T>)JsonContext.GetTypeInfo(typeof(T))!;

            return JsonSerializer.SerializeToElement(value, typeInfo);
        }

        private static Dictionary<string, JsonElement> ReadEditedValues(
            string path, uint expectedVersion, string expectedLayout)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Variable JSON file was not found.", path);

            string extension = Path.GetExtension(path);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            void AddRecords(JsonElement records)
            {
                if (records.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The JSON 'variables' property must be an array.");

                foreach (JsonElement record in records.EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object ||
                        !record.TryGetProperty("id", out JsonElement idElement) ||
                        idElement.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Each variable record must contain a string 'id'.");

                    string id = idElement.GetString()!;
                    if (!values.TryAdd(id, record.Clone()))
                        throw new InvalidDataException($"Duplicate variable ID in JSON: {id}");
                }
            }

            if (extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    using JsonDocument document = JsonDocument.Parse(line);
                    AddSingleRecord(document.RootElement, values, expectedVersion, expectedLayout);
                }
            }
            else
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    AddRecords(root);
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("variables", out JsonElement records))
                {
                    if (root.TryGetProperty("format", out JsonElement formatElement) &&
                        formatElement.ValueKind == JsonValueKind.String &&
                        formatElement.GetString() != JsonFormat)
                        throw new InvalidDataException("Unrecognized YSVR JSON format.");

                    if (root.TryGetProperty("version", out JsonElement versionElement) &&
                        versionElement.ValueKind == JsonValueKind.Number &&
                        versionElement.GetUInt32() != expectedVersion)
                        throw new InvalidDataException("YSVR JSON was exported from a different source version.");

                    if (root.TryGetProperty("layout", out JsonElement layoutElement) &&
                        layoutElement.ValueKind == JsonValueKind.String &&
                        layoutElement.GetString() != expectedLayout)
                        throw new InvalidDataException("YSVR JSON uses a different record layout.");

                    AddRecords(records);
                }
                else
                {
                    throw new InvalidDataException(
                        "Expected a JSON object containing a 'variables' array.");
                }
            }

            return values;
        }

        private static void AddSingleRecord(
            JsonElement record,
            Dictionary<string, JsonElement> values,
            uint expectedVersion,
            string expectedLayout)
        {
            if (record.ValueKind != JsonValueKind.Object ||
                !record.TryGetProperty("id", out JsonElement idElement) ||
                idElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Each JSONL variable record must contain a string 'id'.");

            if (record.TryGetProperty("format", out JsonElement formatElement) &&
                formatElement.ValueKind == JsonValueKind.String &&
                formatElement.GetString() != JsonFormat)
                throw new InvalidDataException("Unrecognized YSVR JSONL format.");

            if (record.TryGetProperty("version", out JsonElement versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number &&
                versionElement.GetUInt32() != expectedVersion)
                throw new InvalidDataException("YSVR JSONL was exported from a different source version.");

            if (record.TryGetProperty("layout", out JsonElement layoutElement) &&
                layoutElement.ValueKind == JsonValueKind.String &&
                layoutElement.GetString() != expectedLayout)
                throw new InvalidDataException("YSVR JSONL uses a different record layout.");

            string id = idElement.GetString()!;
            if (!values.TryAdd(id, record.Clone()))
                throw new InvalidDataException($"Duplicate variable ID in JSONL: {id}");
        }

        private byte[] SerializeStringExpression(string value)
        {
            string quoted;
            if (!value.Contains('`'))
                quoted = $"`{StringUtil.EscapeC(value)}`";
            else if (!value.Contains('"'))
                quoted = $"\"{StringUtil.EscapeC(value)}\"";
            else if (!value.Contains('\''))
                quoted = $"'{StringUtil.EscapeC(value)}'";
            else
                throw new InvalidDataException(
                    "YU-RIS expression strings cannot contain all three quote delimiters.");

            byte[] encoded = _encoding.WriteEncoding.GetBytes(quoted);
            if (encoded.Length > ushort.MaxValue)
                throw new InvalidDataException("YU-RIS string expression is too large.");

            byte[] expression = new byte[3 + encoded.Length];
            expression[0] = 0x4D; // pushstring
            BinaryPrimitives.WriteUInt16LittleEndian(
                expression.AsSpan(1, 2), (ushort)encoded.Length);
            Buffer.BlockCopy(encoded, 0, expression, 3, encoded.Length);
            return expression;
        }

        private static byte[] ApplyPatches(byte[] original, List<BinaryPatch> patches)
        {
            if (patches.Count == 0)
                return (byte[])original.Clone();

            patches.Sort((left, right) => left.Offset.CompareTo(right.Offset));

            using var output = new MemoryStream(original.Length);
            int sourceOffset = 0;

            foreach (BinaryPatch patch in patches)
            {
                if (patch.Offset < sourceOffset ||
                    patch.OriginalLength < 0 ||
                    patch.Offset > original.Length - patch.OriginalLength)
                    throw new InvalidDataException("YSVR patch ranges overlap or exceed the source file.");

                output.Write(original, sourceOffset, patch.Offset - sourceOffset);
                output.Write(patch.Replacement, 0, patch.Replacement.Length);
                sourceOffset = patch.Offset + patch.OriginalLength;
            }

            output.Write(original, sourceOffset, original.Length - sourceOffset);
            return output.ToArray();
        }

        private static string GetScopeName(byte scope)
        {
            return scope switch
            {
                (byte)YurisVariableScope.Global => "global",
                (byte)YurisVariableScope.Local => "local",
                (byte)YurisVariableScope.Static => "static",
                _ => $"unknown-{scope}"
            };
        }

        private static string GetTypeName(YurisVariableValueType type)
        {
            return type switch
            {
                YurisVariableValueType.Uninitialized => "uninitialized",
                YurisVariableValueType.Integer => "integer",
                YurisVariableValueType.Double => "double",
                YurisVariableValueType.StringExpression => "stringExpression",
                _ => $"unknown-{(byte)type}"
            };
        }

        private static bool TryParseRecords(
            byte[] data,
            int count,
            bool hasFlag,
            out List<YurisVariableDeclaration>? variables,
            out int consumed,
            out string? error)
        {
            variables = null;
            consumed = HeaderSize;
            error = null;

            try
            {
                int headerLength = hasFlag ? 8 : 7;
                var parsed = new List<YurisVariableDeclaration>(count);

                for (int i = 0; i < count; i++)
                {
                    EnsureAvailable(data, consumed, headerLength, $"YSVR variable {i} header");

                    byte scope = data[consumed];
                    byte flag = hasFlag ? data[consumed + 1] : (byte)0;
                    int fileIdOffset = consumed + (hasFlag ? 2 : 1);
                    ushort scriptId = ReadUInt16(data, fileIdOffset);
                    ushort index = ReadUInt16(data, fileIdOffset + 2);
                    int typeOffset = fileIdOffset + 4;
                    byte rawType = data[typeOffset];
                    int dimensionCount = data[typeOffset + 1];

                    if (scope < (byte)YurisVariableScope.Global ||
                        scope > (byte)YurisVariableScope.Static)
                        throw new InvalidDataException(
                            $"YSVR variable {i} has an unknown scope value ({scope}).");

                    if (rawType > (byte)YurisVariableValueType.StringExpression)
                        throw new InvalidDataException(
                            $"YSVR variable {i} has an unknown value type ({rawType}).");

                    if (dimensionCount > MaxDimensions)
                        throw new InvalidDataException(
                            $"YSVR variable {i} has too many dimensions ({dimensionCount}).");

                    consumed += headerLength;

                    var dimensions = new List<uint>(dimensionCount);
                    for (int dimension = 0; dimension < dimensionCount; dimension++)
                    {
                        EnsureAvailable(data, consumed, 4,
                            $"YSVR variable {i} dimension {dimension}");
                        dimensions.Add(ReadUInt32(data, consumed));
                        consumed += 4;
                    }

                    long? integerValue = null;
                    double? doubleValue = null;
                    byte[]? stringExpression = null;
                    string? evaluatedStringValue = null;
                    int valueOffset = -1;
                    int valueLength = 0;
                    int stringLengthOffset = -1;

                    switch ((YurisVariableValueType)rawType)
                    {
                        case YurisVariableValueType.Uninitialized:
                            break;

                        case YurisVariableValueType.Integer:
                            EnsureAvailable(data, consumed, 8, $"YSVR variable {i} integer value");
                            valueOffset = consumed;
                            valueLength = 8;
                            integerValue = BinaryPrimitives.ReadInt64LittleEndian(
                                data.AsSpan(consumed, 8));
                            consumed += 8;
                            break;

                        case YurisVariableValueType.Double:
                            EnsureAvailable(data, consumed, 8, $"YSVR variable {i} double value");
                            valueOffset = consumed;
                            valueLength = 8;
                            long doubleBits = BinaryPrimitives.ReadInt64LittleEndian(
                                data.AsSpan(consumed, 8));
                            doubleValue = BitConverter.Int64BitsToDouble(doubleBits);
                            consumed += 8;
                            break;

                        case YurisVariableValueType.StringExpression:
                            EnsureAvailable(data, consumed, 2, $"YSVR variable {i} string length");
                            stringLengthOffset = consumed;
                            int expressionLength = ReadUInt16(data, consumed);
                            consumed += 2;
                            valueOffset = consumed;
                            valueLength = expressionLength;
                            EnsureAvailable(data, consumed, expressionLength,
                                $"YSVR variable {i} string expression");
                            stringExpression = data.AsSpan(consumed, expressionLength).ToArray();
                            consumed += expressionLength;

                            if (stringExpression.Length > 0 &&
                                YurisExpressionInterpreter.TryEvaluate(
                                    stringExpression, 0, stringExpression.Length,
                                    out string? evaluated))
                            {
                                evaluatedStringValue = evaluated;
                            }
                            break;
                    }

                    parsed.Add(new YurisVariableDeclaration(
                        scope,
                        flag,
                        scriptId,
                        index,
                        (YurisVariableValueType)rawType,
                        dimensions.AsReadOnly(),
                        integerValue,
                        doubleValue,
                        stringExpression,
                        evaluatedStringValue,
                        valueOffset,
                        valueLength,
                        stringLengthOffset));
                }

                for (int i = consumed; i < data.Length; i++)
                {
                    if (data[i] != 0)
                        throw new InvalidDataException(
                            $"YSVR has nonzero trailing data at offset 0x{i:X}.");
                }

                variables = parsed;
                return true;
            }
            catch (InvalidDataException exception)
            {
                error = exception.Message;
                return false;
            }
            catch (ArgumentOutOfRangeException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void EnsureAvailable(byte[] data, int offset, int length, string context)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length)
                throw new InvalidDataException($"{context} is truncated.");
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        }

        private sealed class BinaryPatch
        {
            public int Offset { get; }
            public int OriginalLength { get; }
            public byte[] Replacement { get; }

            public BinaryPatch(int offset, int originalLength, byte[] replacement)
            {
                Offset = offset;
                OriginalLength = originalLength;
                Replacement = replacement;
            }
        }

        private sealed class VariableExportDocument
        {
            public string Format { get; set; } = JsonFormat;
            public uint Version { get; set; }
            public string Layout { get; set; } = string.Empty;
            public List<VariableExportRecord> Variables { get; set; } = new();
        }

        private sealed class VariableExportRecord
        {
            public string Format { get; set; } = JsonFormat;
            public uint Version { get; set; }
            public string Layout { get; set; } = string.Empty;
            public string Id { get; set; } = string.Empty;
            public string Scope { get; set; } = string.Empty;
            public byte ScopeValue { get; set; }
            public byte Flag { get; set; }
            public ushort ScriptId { get; set; }
            public ushort Index { get; set; }
            public string Type { get; set; } = string.Empty;
            public byte TypeValue { get; set; }
            public uint[] Dimensions { get; set; } = Array.Empty<uint>();
            public JsonElement? Value { get; set; }
            public bool IsEvaluated { get; set; }
            public string? ExpressionBase64 { get; set; }
        }

        [JsonSourceGenerationOptions(
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        [JsonSerializable(typeof(VariableExportDocument))]
        [JsonSerializable(typeof(VariableExportRecord))]
        [JsonSerializable(typeof(long))]
        [JsonSerializable(typeof(double))]
        [JsonSerializable(typeof(string))]
        private partial class VariableJsonContext : JsonSerializerContext
        {
        }
    }
}
