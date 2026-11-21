using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
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
        /// Raw expression bytecode for string initializers. Kept intact so
        /// unsupported/dynamic expressions are not lost.
        /// </summary>
        public byte[]? StringExpression { get; }

        /// <summary>
        /// Statically evaluated string initializer, when the expression
        /// consists entirely of supported constant operations.
        /// </summary>
        public string? EvaluatedStringValue { get; }

        public bool IsArray => Dimensions.Count > 0;

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
            string? evaluatedStringValue)
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
        }
    }

    /// <summary>
    /// Parser and IScript adapter for YU-RIS variable initialization data
    /// (ysv.ybn, magic YSVR).
    ///
    /// Record layouts found in real engine versions:
    ///   WithFlag:    kind, flag, fileId(u16), slot(u16), type, dimensions
    ///   WithoutFlag: kind, fileId(u16), slot(u16), type, dimensions
    ///
    /// Both layouts are validated to the end of the file. Version is used
    /// only to break a tie when both interpretations are structurally valid.
    /// </summary>
    public sealed class YurisVariableScript : IScript
    {
        private const int HeaderSize = 10;
        private const int MaxDimensions = 32;

        public string Extension => ".ybn";

        public uint Version { get; private set; }

        public YurisVariableRecordLayout Layout { get; private set; }

        public IReadOnlyList<YurisVariableDeclaration> Variables { get; private set; }
            = Array.Empty<YurisVariableDeclaration>();

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
            byte[] data = File.ReadAllBytes(location.ToFilePath());
            Parse(data);
        }

        /// <summary>
        /// Parse a complete YSVR file from memory.
        /// </summary>
        public void Parse(byte[] data)
        {
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
                out int withFlagEnd,
                out string? withFlagError);

            bool withoutFlagValid = TryParseRecords(
                data, variableCount, hasFlag: false,
                out List<YurisVariableDeclaration>? withoutFlag,
                out int withoutFlagEnd,
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
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            // YSVR is initialization metadata, not a text resource.
            yield break;
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            using IEnumerator<ScriptString> enumerator = strings.GetEnumerator();
            if (enumerator.MoveNext())
                throw new InvalidDataException("YSVR contains no translatable strings.");
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

                    switch ((YurisVariableValueType)rawType)
                    {
                        case YurisVariableValueType.Uninitialized:
                            // A declaration without an initializer has no value payload.
                            break;

                        case YurisVariableValueType.Integer:
                            EnsureAvailable(data, consumed, 8, $"YSVR variable {i} integer value");
                            integerValue = BinaryPrimitives.ReadInt64LittleEndian(
                                data.AsSpan(consumed, 8));
                            consumed += 8;
                            break;

                        case YurisVariableValueType.Double:
                            EnsureAvailable(data, consumed, 8, $"YSVR variable {i} double value");
                            long doubleBits = BinaryPrimitives.ReadInt64LittleEndian(
                                data.AsSpan(consumed, 8));
                            doubleValue = BitConverter.Int64BitsToDouble(doubleBits);
                            consumed += 8;
                            break;

                        case YurisVariableValueType.StringExpression:
                            EnsureAvailable(data, consumed, 2, $"YSVR variable {i} string length");
                            int expressionLength = ReadUInt16(data, consumed);
                            consumed += 2;
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
                        evaluatedStringValue));
                }

                // Some builds leave zero-filled alignment bytes; nonzero trailing
                // bytes are not silently accepted as another record.
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
    }
}
