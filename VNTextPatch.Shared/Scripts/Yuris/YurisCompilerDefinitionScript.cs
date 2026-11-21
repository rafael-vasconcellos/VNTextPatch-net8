using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Buffers.Binary;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// One YSCom compiler command parameter. The four metadata bytes are
    /// preserved exactly; their meaning can vary between compiler versions.
    /// </summary>
    public sealed class YurisCompilerParameter
    {
        public string Name { get; }
        public byte Kind { get; }
        public byte Metadata1 { get; }
        public byte Metadata2 { get; }
        public byte Checker { get; }

        internal YurisCompilerParameter(
            string name, byte kind, byte metadata1, byte metadata2, byte checker)
        {
            Name = name;
            Kind = kind;
            Metadata1 = metadata1;
            Metadata2 = metadata2;
            Checker = checker;
        }
    }

    /// <summary>
    /// One command declaration from YSCom.ycd.
    /// </summary>
    public sealed class YurisCompilerCommand
    {
        public uint Index { get; }
        public string Name { get; }
        public IReadOnlyList<YurisCompilerParameter> Parameters { get; }
        public bool IsPlaceholder => Name.Length == 0 || Name == ");";

        internal YurisCompilerCommand(
            uint index, string name, IReadOnlyList<YurisCompilerParameter> parameters)
        {
            Index = index;
            Name = name;
            Parameters = parameters;
        }
    }

    /// <summary>
    /// One system variable declaration from YSCom.ycd.
    /// The declaration order is the system-variable ID order (zero-based).
    /// </summary>
    public sealed class YurisCompilerSystemVariable
    {
        public uint Id { get; }
        public string Name { get; }
        public byte ValueType { get; }
        public IReadOnlyList<uint> Dimensions { get; }
        public bool IsArray => Dimensions.Count > 0;

        internal YurisCompilerSystemVariable(
            uint id, string name, byte valueType, IReadOnlyList<uint> dimensions)
        {
            Id = id;
            Name = name;
            ValueType = valueType;
            Dimensions = dimensions;
        }
    }

    /// <summary>
    /// Read-only parser for the YU-RIS compiler language definition file
    /// (YSCom.ycd, magic YSCD).
    ///
    /// It decodes the command table and system-variable table, while retaining
    /// the intermediate and trailing regions as raw bytes. Optional strings
    /// (settings, keywords and error messages) are exposed as metadata only.
    /// </summary>
    public sealed class YurisCompilerDefinitionScript : IScript
    {
        private const int HeaderSize = 0x10;
        private const int MaxCommands = 4096;
        private const int MaxSystemVariables = 8192;
        private const int MaxDimensions = 32;
        private const int KnownExtraErrorCount = 37;

        private Dictionary<string, uint> _commandIds =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private Dictionary<string, uint> _systemVariableIds =
            new Dictionary<string, uint>(StringComparer.Ordinal);

        public string Extension => ".ycd";

        public uint Version { get; private set; }
        public uint HeaderCommandCount { get; private set; }
        public uint HeaderUnknown { get; private set; }

        /// <summary>
        /// Commands in file order. The original header count can include
        /// placeholder entries which are intentionally retained.
        /// </summary>
        public IReadOnlyList<YurisCompilerCommand> Commands { get; private set; }
            = Array.Empty<YurisCompilerCommand>();

        public IReadOnlyList<YurisCompilerCommand> ExecutableCommands =>
            Commands.Where(command => !command.IsPlaceholder).ToArray();

        /// <summary>
        /// System variables in declaration order; position is the variable ID.
        /// </summary>
        public IReadOnlyList<YurisCompilerSystemVariable> SystemVariables { get; private set; }
            = Array.Empty<YurisCompilerSystemVariable>();

        /// <summary>
        /// Candidate setting names found in the metadata region between the
        /// command and system-variable sections.
        /// </summary>
        public IReadOnlyList<string> Settings { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Candidate parameter keywords found in the same metadata region.
        /// The raw region is also retained to avoid losing unknown structures.
        /// </summary>
        public IReadOnlyList<string> Keywords { get; private set; } = Array.Empty<string>();

        public IReadOnlyList<string> ErrorMessages { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> ExtraErrorMessages { get; private set; } = Array.Empty<string>();

        public int CommandSectionEnd { get; private set; }
        public int SystemVariableSectionOffset { get; private set; }
        public int SystemVariableSectionEnd { get; private set; }

        public byte[] DefinitionMetadata { get; private set; } = Array.Empty<byte>();
        public byte[] TrailingData { get; private set; } = Array.Empty<byte>();

        public void Load(ScriptLocation location)
        {
            Parse(File.ReadAllBytes(location.ToFilePath()));
        }

        /// <summary>
        /// Parses a complete YSCD file from memory.
        /// </summary>
        public void Parse(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length < HeaderSize)
                throw new InvalidDataException("YSCom.ycd header is truncated.");

            if (!data.AsSpan(0, 4).SequenceEqual("YSCD"u8))
                throw new InvalidDataException("Invalid YSCD signature.");

            uint version = ReadUInt32(data, 4);
            uint commandCount = ReadUInt32(data, 8);
            uint headerUnknown = ReadUInt32(data, 12);

            if (commandCount > MaxCommands)
                throw new InvalidDataException(
                    $"YSCom.ycd declares an unreasonable command count ({commandCount}).");

            int offset = HeaderSize;
            var commands = new List<YurisCompilerCommand>((int)commandCount);
            var commandIds = new Dictionary<string, uint>(StringComparer.Ordinal);

            for (uint commandIndex = 0; commandIndex < commandCount; commandIndex++)
            {
                string commandName = ReadZeroTerminatedAscii(
                    data, ref offset, $"command {commandIndex} name");

                EnsureAvailable(data, offset, 1, $"command {commandIndex} parameter count");
                byte parameterCount = data[offset++];

                var parameters = new List<YurisCompilerParameter>(parameterCount);
                for (int parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
                {
                    string parameterName = ReadZeroTerminatedAscii(
                        data, ref offset,
                        $"parameter {parameterIndex} of command '{commandName}'");

                    EnsureAvailable(data, offset, 4,
                        $"metadata of parameter '{parameterName}'");

                    parameters.Add(new YurisCompilerParameter(
                        parameterName,
                        data[offset],
                        data[offset + 1],
                        data[offset + 2],
                        data[offset + 3]));
                    offset += 4;
                }

                var command = new YurisCompilerCommand(
                    commandIndex, commandName, parameters.AsReadOnly());
                commands.Add(command);

                if (!command.IsPlaceholder)
                    commandIds.TryAdd(commandName, commandIndex);
            }

            int commandSectionEnd = offset;
            if (!TryLocateSystemVariableSection(
                data,
                commandSectionEnd,
                out int systemVariableOffset,
                out List<YurisCompilerSystemVariable>? systemVariables,
                out int systemVariableEnd,
                out string? locateError))
            {
                throw new InvalidDataException(
                    $"Could not locate the YSCD system-variable section: {locateError}");
            }

            byte[] definitionMetadata = data
                .AsSpan(commandSectionEnd, systemVariableOffset - commandSectionEnd)
                .ToArray();

            ExtractMetadataStrings(
                definitionMetadata,
                out List<string> settings,
                out List<string> keywords);

            ParseTrailingSections(
                data,
                systemVariableEnd,
                out List<string> errorMessages,
                out List<string> extraErrorMessages,
                out int trailingOffset);

            var variableIds = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (YurisCompilerSystemVariable variable in systemVariables!)
                variableIds.TryAdd(variable.Name, variable.Id);

            // Publish only after every mandatory section has been validated.
            Version = version;
            HeaderCommandCount = commandCount;
            HeaderUnknown = headerUnknown;
            Commands = commands.AsReadOnly();
            SystemVariables = systemVariables!.AsReadOnly();
            Settings = settings.AsReadOnly();
            Keywords = keywords.AsReadOnly();
            ErrorMessages = errorMessages.AsReadOnly();
            ExtraErrorMessages = extraErrorMessages.AsReadOnly();
            CommandSectionEnd = commandSectionEnd;
            SystemVariableSectionOffset = systemVariableOffset;
            SystemVariableSectionEnd = systemVariableEnd;
            DefinitionMetadata = definitionMetadata;
            TrailingData = data.AsSpan(trailingOffset).ToArray();

            _commandIds = commandIds;
            _systemVariableIds = variableIds;
        }

        public bool TryGetCommandId(string name, out uint commandId)
        {
            return _commandIds.TryGetValue(name, out commandId);
        }

        public uint GetCommandId(string name)
        {
            if (!_commandIds.TryGetValue(name, out uint commandId))
                throw new KeyNotFoundException($"YSCD command '{name}' was not found.");
            return commandId;
        }

        public YurisCompilerCommand? GetCommand(uint commandId)
        {
            return commandId < Commands.Count ? Commands[(int)commandId] : null;
        }

        public YurisCompilerCommand? GetCommand(string name)
        {
            return _commandIds.TryGetValue(name, out uint id) ? GetCommand(id) : null;
        }

        public YurisCompilerParameter? GetParameter(string commandName, int parameterIndex)
        {
            if (parameterIndex < 0)
                return null;

            return GetCommand(commandName)?.Parameters.ElementAtOrDefault(parameterIndex);
        }

        public bool TryGetSystemVariableId(string name, out uint variableId)
        {
            return _systemVariableIds.TryGetValue(name, out variableId);
        }

        public uint GetSystemVariableId(string name)
        {
            if (!_systemVariableIds.TryGetValue(name, out uint variableId))
                throw new KeyNotFoundException($"YSCD system variable '{name}' was not found.");
            return variableId;
        }

        public YurisCompilerSystemVariable? GetSystemVariable(uint variableId)
        {
            return variableId < SystemVariables.Count
                ? SystemVariables[(int)variableId]
                : null;
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            // Compiler metadata is not a translation resource.
            yield break;
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            using IEnumerator<ScriptString> enumerator = strings.GetEnumerator();
            if (enumerator.MoveNext())
                throw new InvalidDataException("YSCom.ycd contains no translatable strings.");
        }

        private static bool TryLocateSystemVariableSection(
            byte[] data,
            int searchStart,
            out int sectionOffset,
            out List<YurisCompilerSystemVariable>? variables,
            out int sectionEnd,
            out string? error)
        {
            sectionOffset = -1;
            variables = null;
            sectionEnd = -1;
            error = null;

            string? firstError = null;
            for (int candidate = searchStart; candidate <= data.Length - 8; candidate++)
            {
                uint count = ReadUInt32(data, candidate);
                uint reserved = ReadUInt32(data, candidate + 4);

                if (count == 0 || count > MaxSystemVariables || reserved != 0)
                    continue;

                if (!TryParseSystemVariables(
                    data, candidate, (int)count,
                    out List<YurisCompilerSystemVariable>? parsed,
                    out int end,
                    out string? parseError))
                {
                    firstError ??= parseError;
                    continue;
                }

                if (parsed!.Count == 0 ||
                    !parsed[0].Name.StartsWith("_", StringComparison.Ordinal))
                    continue;

                // In known YSCom builds, the variable section ends either at
                // EOF or at another count/reserved section header (error table).
                bool sectionBoundary = end == data.Length ||
                    (end <= data.Length - 8 &&
                     ReadUInt32(data, end + 4) == 0 &&
                     ReadUInt32(data, end) <= 1_000_000);

                if (!sectionBoundary)
                    continue;

                sectionOffset = candidate;
                variables = parsed;
                sectionEnd = end;
                return true;
            }

            error = firstError ?? "No structurally valid system-variable section found.";
            return false;
        }

        private static bool TryParseSystemVariables(
            byte[] data,
            int sectionOffset,
            int count,
            out List<YurisCompilerSystemVariable>? variables,
            out int end,
            out string? error)
        {
            variables = null;
            end = sectionOffset;
            error = null;

            try
            {
                int offset = checked(sectionOffset + 8);
                var parsed = new List<YurisCompilerSystemVariable>(count);

                for (int i = 0; i < count; i++)
                {
                    string name = ReadZeroTerminatedAscii(
                        data, ref offset, $"system variable {i} name");

                    if (name.Length == 0 || name.Length > 256)
                        throw new InvalidDataException(
                            $"YSCD system variable {i} has an invalid name.");

                    EnsureAvailable(data, offset, 2, $"system variable {i} type/dimensions");
                    byte valueType = data[offset++];
                    byte dimensionCount = data[offset++];

                    if (valueType < 1 || valueType > 3)
                        throw new InvalidDataException(
                            $"YSCD system variable '{name}' has an invalid type ({valueType}).");

                    if (dimensionCount > MaxDimensions)
                        throw new InvalidDataException(
                            $"YSCD system variable '{name}' has too many dimensions ({dimensionCount}).");

                    var dimensions = new List<uint>(dimensionCount);
                    for (int dimension = 0; dimension < dimensionCount; dimension++)
                    {
                        EnsureAvailable(data, offset, 4,
                            $"system variable '{name}' dimension {dimension}");
                        dimensions.Add(ReadUInt32(data, offset));
                        offset += 4;
                    }

                    parsed.Add(new YurisCompilerSystemVariable(
                        (uint)i, name, valueType, dimensions.AsReadOnly()));
                }

                variables = parsed;
                end = offset;
                return true;
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is OverflowException ||
                exception is ArgumentOutOfRangeException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void ParseTrailingSections(
            byte[] data,
            int start,
            out List<string> messages,
            out List<string> extraMessages,
            out int trailingOffset)
        {
            messages = new List<string>();
            extraMessages = new List<string>();
            trailingOffset = start;

            // Layout documented in Notes.txt:
            // u32 messageCount, u32 padding, messageCount zero-terminated strings,
            // 37 extra error strings, followed by opaque compiler tables.
            if (start > data.Length - 8)
                return;

            uint count = ReadUInt32(data, start);
            uint reserved = ReadUInt32(data, start + 4);
            if (count > 10000 || reserved != 0)
                return;

            int offset = start + 8;
            var parsedMessages = new List<string>((int)count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    string message = ReadZeroTerminatedString(
                        data, ref offset, $"YSCD compiler message {i}");
                    parsedMessages.Add(message);
                }

                var parsedExtraMessages = new List<string>(KnownExtraErrorCount);
                for (int i = 0; i < KnownExtraErrorCount; i++)
                {
                    string message = ReadZeroTerminatedString(
                        data, ref offset, $"YSCD extra error message {i}");
                    parsedExtraMessages.Add(message);
                }

                messages = parsedMessages;
                extraMessages = parsedExtraMessages;
                trailingOffset = offset;
            }
            catch (InvalidDataException)
            {
                // Some compiler builds omit or rearrange this optional tail.
                // Keep the complete bytes in TrailingData instead.
            }
        }

        private static void ExtractMetadataStrings(
            byte[] data,
            out List<string> settings,
            out List<string> keywords)
        {
            settings = new List<string>();
            keywords = new List<string>();

            var seenSettings = new HashSet<string>(StringComparer.Ordinal);
            var seenKeywords = new HashSet<string>(StringComparer.Ordinal);

            // This region contains string tables and compiler metadata. Scan
            // conservatively for bounded ASCII identifiers and retain the raw
            // bytes separately so no undocumented structure is discarded.
            for (int offset = 0; offset < data.Length;)
            {
                int end = Array.IndexOf(data, (byte)0, offset);
                if (end < 0)
                    break;

                int length = end - offset;
                if (length > 0 && length <= 64 && IsAsciiIdentifier(data, offset, length))
                {
                    string value = Encoding.ASCII.GetString(data, offset, length);
                    bool setting = value.Any(character =>
                        character >= 'A' && character <= 'Z');

                    if (setting)
                    {
                        if (seenSettings.Add(value))
                            settings.Add(value);
                    }
                    else if (seenKeywords.Add(value))
                    {
                        keywords.Add(value);
                    }

                    offset = end + 1;
                }
                else
                {
                    offset++;
                }
            }
        }

        private static bool IsAsciiIdentifier(byte[] data, int offset, int length)
        {
            for (int i = offset; i < offset + length; i++)
            {
                byte value = data[i];
                bool valid = (value >= (byte)'A' && value <= (byte)'Z') ||
                             (value >= (byte)'a' && value <= (byte)'z') ||
                             (value >= (byte)'0' && value <= (byte)'9') ||
                             value == (byte)'_' || value == (byte)'$' ||
                             value == (byte)'#' || value == (byte)';';
                if (!valid)
                    return false;
            }

            return true;
        }

        private static string ReadZeroTerminatedAscii(
            byte[] data, ref int offset, string context)
        {
            int end = Array.IndexOf(data, (byte)0, offset);
            if (end < 0)
                throw new InvalidDataException($"Unterminated string in {context}.");

            int length = end - offset;
            if (length > 512)
                throw new InvalidDataException($"String in {context} is too long.");

            for (int i = offset; i < end; i++)
            {
                if (data[i] > 0x7F)
                    throw new InvalidDataException(
                        $"Non-ASCII byte in YSCD identifier at {context}.");
            }

            string value = Encoding.ASCII.GetString(data, offset, length);
            offset = end + 1;
            return value;
        }

        private static string ReadZeroTerminatedString(
            byte[] data, ref int offset, string context)
        {
            int end = Array.IndexOf(data, (byte)0, offset);
            if (end < 0)
                throw new InvalidDataException($"Unterminated string in {context}.");

            string value = StringUtil.SjisEncoding.GetString(data, offset, end - offset);
            offset = end + 1;
            return value;
        }

        private static void EnsureAvailable(
            byte[] data, int offset, int length, string context)
        {
            if (offset < 0 || length < 0 || offset > data.Length - length)
                throw new InvalidDataException($"{context} is truncated.");
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        }
    }
}
