using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Conservative evaluator for the small YU-RIS expression VM used in
    /// compiled script attributes. Only expressions whose result is completely
    /// determined by constants are evaluated.
    /// </summary>
    internal static class YurisExpressionInterpreter
    {
        private enum ValueKind
        {
            Number,
            String,
            CharacterBytes
        }

        private readonly struct Value
        {
            public ValueKind Kind { get; }
            public double Number { get; }
            public string? Text { get; }
            public byte[]? Bytes { get; }
            public byte VariableScope { get; }
            public ushort VariableId { get; }

            private Value(
                ValueKind kind,
                double number = 0,
                string? text = null,
                byte[]? bytes = null,
                byte variableScope = 0,
                ushort variableId = 0)
            {
                Kind = kind;
                Number = number;
                Text = text;
                Bytes = bytes;
                VariableScope = variableScope;
                VariableId = variableId;
            }

            public static Value FromNumber(double value) =>
                new Value(ValueKind.Number, number: value);

            public static Value FromString(string value) =>
                new Value(ValueKind.String, text: value);

            public static Value FromCharacterByte(byte value, byte scope, ushort variableId) =>
                new Value(
                    ValueKind.CharacterBytes,
                    bytes: new[] { value },
                    variableScope: scope,
                    variableId: variableId);

            public static Value FromCharacterBytes(
                byte[] value, byte scope, ushort variableId) =>
                new Value(
                    ValueKind.CharacterBytes,
                    bytes: value,
                    variableScope: scope,
                    variableId: variableId);
        }

        private readonly struct PreparedVariable
        {
            public byte Scope { get; }
            public ushort Id { get; }
            public int StackDepth { get; }

            public PreparedVariable(byte scope, ushort id, int stackDepth)
            {
                Scope = scope;
                Id = id;
                StackDepth = stackDepth;
            }
        }

        /// <summary>
        /// Attempts to evaluate one complete expression. Unsupported opcodes,
        /// dynamic variables, malformed instructions and non-string results
        /// return false without modifying the input buffer.
        /// </summary>
        public static bool TryEvaluate(
            byte[] data, int offset, int length, out string? result)
        {
            result = null;

            if (data == null || offset < 0 || length <= 0 ||
                offset > data.Length - length)
                return false;

            int end = offset + length;
            int position = offset;
            var stack = new List<Value>();
            PreparedVariable? preparedVariable = null;

            while (position < end)
            {
                if (end - position < 3)
                    return false;

                byte opcode = data[position];
                int argumentLength = data[position + 1] |
                                     (data[position + 2] << 8);
                int argumentOffset = position + 3;
                int nextPosition = argumentOffset + argumentLength;

                if (nextPosition < argumentOffset || nextPosition > end)
                    return false;

                switch (opcode)
                {
                    // pushint8
                    case 0x42:
                        if (argumentLength != 1)
                            return false;
                        stack.Add(Value.FromNumber(unchecked((sbyte)data[argumentOffset])));
                        break;

                    // pushdouble
                    case 0x46:
                        if (argumentLength != 8)
                            return false;
                        double doubleValue = BitConverter.ToDouble(data, argumentOffset);
                        if (!IsFinite(doubleValue))
                            return false;
                        stack.Add(Value.FromNumber(doubleValue));
                        break;

                    // pushint32
                    case 0x49:
                        if (argumentLength != 4)
                            return false;
                        stack.Add(Value.FromNumber(BitConverter.ToInt32(data, argumentOffset)));
                        break;

                    // pushint64
                    case 0x4C:
                        if (argumentLength != 8)
                            return false;
                        stack.Add(Value.FromNumber(BitConverter.ToInt64(data, argumentOffset)));
                        break;

                    // pushstring
                    case 0x4D:
                        if (!TryReadString(data, argumentOffset, argumentLength, out string? text))
                            return false;
                        stack.Add(Value.FromString(text));
                        break;

                    // pushint16
                    case 0x57:
                        if (argumentLength != 2)
                            return false;
                        stack.Add(Value.FromNumber(BitConverter.ToInt16(data, argumentOffset)));
                        break;

                    // preparevarindexation. The variable itself is not read;
                    // only constant indices into a byte/character lookup can
                    // be folded below.
                    case 0x56:
                        if (argumentLength != 3 || preparedVariable.HasValue)
                            return false;
                        preparedVariable = new PreparedVariable(
                            data[argumentOffset],
                            BitConverter.ToUInt16(data, argumentOffset + 1),
                            stack.Count);
                        break;

                    // performvarindexation. This is statically resolvable only
                    // for a single constant byte index. The resulting byte
                    // sequence is accepted as text only when it contains a
                    // recognized YU-RIS control-code pair.
                    case 0x29:
                        if (argumentLength != 1 ||
                            data[argumentOffset] != 0 ||
                            !preparedVariable.HasValue)
                            return false;

                        PreparedVariable variable = preparedVariable.Value;
                        if (stack.Count != variable.StackDepth + 1 ||
                            stack[stack.Count - 1].Kind != ValueKind.Number)
                            return false;

                        double indexValue = stack[stack.Count - 1].Number;
                        if (!IsInteger(indexValue) || indexValue < 0 || indexValue > byte.MaxValue)
                            return false;

                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(Value.FromCharacterByte(
                            (byte)indexValue, variable.Scope, variable.Id));
                        preparedVariable = null;
                        break;

                    // add (numeric addition, string concatenation or
                    // concatenation of constant bytes from the same variable).
                    case 0x2B:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            !TryAdd(stack))
                            return false;
                        break;

                    // sub
                    case 0x2D:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            !TryNumericBinary(stack, (a, b) => a - b))
                            return false;
                        break;

                    // mul
                    case 0x2A:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            !TryNumericBinary(stack, (a, b) => a * b))
                            return false;
                        break;

                    // div
                    case 0x2F:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            !TryNumericBinary(stack, (a, b) => b == 0 ? double.NaN : a / b))
                            return false;
                        break;

                    // mod
                    case 0x25:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            !TryNumericBinary(stack, (a, b) => b == 0 ? double.NaN : a % b))
                            return false;
                        break;

                    // changesign
                    case 0x52:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            stack.Count == 0 ||
                            stack[stack.Count - 1].Kind != ValueKind.Number)
                            return false;
                        stack[stack.Count - 1] =
                            Value.FromNumber(-stack[stack.Count - 1].Number);
                        break;

                    // tonumber
                    case 0x69:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            stack.Count == 0 ||
                            stack[stack.Count - 1].Kind != ValueKind.String ||
                            !double.TryParse(
                                stack[stack.Count - 1].Text,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out double number) ||
                            !IsFinite(number))
                            return false;
                        stack[stack.Count - 1] = Value.FromNumber(number);
                        break;

                    // tostring
                    case 0x73:
                        if (argumentLength != 0 || preparedVariable.HasValue ||
                            stack.Count == 0 ||
                            stack[stack.Count - 1].Kind != ValueKind.Number)
                            return false;
                        stack[stack.Count - 1] =
                            Value.FromString(FormatNumber(stack[stack.Count - 1].Number));
                        break;

                    // All other instructions (notably scalar/array variable
                    // reads and logical/comparison operators) are intentionally
                    // left unevaluated.
                    default:
                        return false;
                }

                position = nextPosition;
            }

            if (preparedVariable.HasValue || stack.Count != 1)
                return false;

            Value finalValue = stack[0];
            if (finalValue.Kind == ValueKind.String)
            {
                result = NormalizeLineBreaks(finalValue.Text ?? string.Empty);
                return true;
            }

            if (finalValue.Kind == ValueKind.CharacterBytes &&
                TryDecodeCharacterBytes(finalValue.Bytes!, out string decoded))
            {
                result = NormalizeLineBreaks(decoded);
                return true;
            }

            // A bare numeric expression is not necessarily text. Requiring an
            // explicit tostring (or string concatenation) avoids extracting
            // constants used as script parameters.
            return false;
        }

        private static bool TryReadString(
            byte[] data, int offset, int length, [NotNullWhen(true)] out string? value)
        {
            value = null;
            if (length < 2)
                return false;

            string encoded = StringUtil.SjisEncoding.GetString(data, offset, length);
            char quote = encoded[0];
            if ((quote != '"' && quote != '\'' && quote != '`') ||
                encoded[encoded.Length - 1] != quote)
                return false;

            value = StringUtil.UnescapeC(encoded.Substring(1, encoded.Length - 2));
            return true;
        }

        private static bool TryAdd(List<Value> stack)
        {
            if (stack.Count < 2)
                return false;

            Value right = stack[stack.Count - 1];
            Value left = stack[stack.Count - 2];

            if (left.Kind == ValueKind.Number && right.Kind == ValueKind.Number)
            {
                double sum = left.Number + right.Number;
                if (!IsFinite(sum))
                    return false;

                stack.RemoveRange(stack.Count - 2, 2);
                stack.Add(Value.FromNumber(sum));
                return true;
            }

            if (left.Kind == ValueKind.String || right.Kind == ValueKind.String)
            {
                if (left.Kind == ValueKind.CharacterBytes ||
                    right.Kind == ValueKind.CharacterBytes)
                    return false;

                string leftText = left.Kind == ValueKind.String
                    ? left.Text!
                    : FormatNumber(left.Number);
                string rightText = right.Kind == ValueKind.String
                    ? right.Text!
                    : FormatNumber(right.Number);

                stack.RemoveRange(stack.Count - 2, 2);
                stack.Add(Value.FromString(leftText + rightText));
                return true;
            }

            if (left.Kind == ValueKind.CharacterBytes &&
                right.Kind == ValueKind.CharacterBytes &&
                left.VariableScope == right.VariableScope &&
                left.VariableId == right.VariableId)
            {
                byte[] combined = new byte[left.Bytes!.Length + right.Bytes!.Length];
                Buffer.BlockCopy(left.Bytes, 0, combined, 0, left.Bytes.Length);
                Buffer.BlockCopy(
                    right.Bytes, 0, combined, left.Bytes.Length, right.Bytes.Length);

                stack.RemoveRange(stack.Count - 2, 2);
                stack.Add(Value.FromCharacterBytes(
                    combined, left.VariableScope, left.VariableId));
                return true;
            }

            return false;
        }

        private static bool TryNumericBinary(
            List<Value> stack, Func<double, double, double> operation)
        {
            if (stack.Count < 2 ||
                stack[stack.Count - 2].Kind != ValueKind.Number ||
                stack[stack.Count - 1].Kind != ValueKind.Number)
                return false;

            double result = operation(
                stack[stack.Count - 2].Number,
                stack[stack.Count - 1].Number);

            if (!IsFinite(result))
                return false;

            stack.RemoveRange(stack.Count - 2, 2);
            stack.Add(Value.FromNumber(result));
            return true;
        }

        private static bool TryDecodeCharacterBytes(byte[] bytes, out string result)
        {
            result = string.Empty;
            if (bytes.Length < 2)
                return false;

            byte[] decodedBytes = (byte[])bytes.Clone();
            bool foundControlCode = false;

            for (int i = 0; i < decodedBytes.Length - 1;)
            {
                if (decodedBytes[i] != 0xEF)
                {
                    i += StringUtil.IsShiftJisLeadByte(decodedBytes[i]) ? 2 : 1;
                    continue;
                }

                switch (decodedBytes[i + 1])
                {
                    case 0xF0:
                        decodedBytes[i] = (byte)'\r';
                        decodedBytes[i + 1] = (byte)'\n';
                        foundControlCode = true;
                        break;
                    case 0xF2:
                        decodedBytes[i] = (byte)'\\';
                        decodedBytes[i + 1] = (byte)'p';
                        foundControlCode = true;
                        break;
                    case 0xF3:
                        decodedBytes[i] = (byte)'\\';
                        decodedBytes[i + 1] = (byte)'c';
                        foundControlCode = true;
                        break;
                    case 0xF5:
                        decodedBytes[i] = (byte)'\\';
                        decodedBytes[i + 1] = (byte)'u';
                        foundControlCode = true;
                        break;
                }

                i += 2;
            }

            if (!foundControlCode)
                return false;

            result = StringUtil.SjisEncoding.GetString(decodedBytes);
            return true;
        }

        private static string NormalizeLineBreaks(string value)
        {
            return value.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }

        private static bool IsInteger(double value)
        {
            return IsFinite(value) && Math.Truncate(value) == value;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
