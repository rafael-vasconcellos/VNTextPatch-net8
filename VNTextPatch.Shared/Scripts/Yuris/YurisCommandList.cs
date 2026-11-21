using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Parses the YU-RIS command definition file (YSCM / ysc.ybn).
    ///
    /// The file contains a 16-byte header, a variable-sized command table,
    /// a sequence of zero-terminated error messages, and a 256-byte table.
    /// The number of error messages is not stored in the header, so it is
    /// derived from the start of the final 256-byte table.
    /// </summary>
    public sealed class YurisCommandList
    {
        private const int HeaderSize = 0x10;
        private const int UnknownTableSize = 0x100;

        private readonly Dictionary<string, byte> _commandIds =
            new Dictionary<string, byte>(StringComparer.Ordinal);

        private IReadOnlyList<YurisCommandDefinition> _commands =
            Array.Empty<YurisCommandDefinition>();

        private IReadOnlyList<string> _errorMessages = Array.Empty<string>();
        private byte[] _unknownTable = Array.Empty<byte>();

        public uint Version { get; private set; }

        /// <summary>
        /// The final 32-bit header field. RxYuris labels this field as unknown.
        /// </summary>
        public uint HeaderUnknown { get; private set; }

        public IReadOnlyList<YurisCommandDefinition> Commands => _commands;

        public IReadOnlyList<string> ErrorMessages => _errorMessages;

        /// <summary>
        /// The opaque 0x100-byte table following the error messages.
        /// </summary>
        public ReadOnlyMemory<byte> UnknownTable => _unknownTable;

        public YurisCommandList(string filePath)
        {
            Load(filePath);
        }

        public void Load(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);

            if (data.Length < HeaderSize + UnknownTableSize)
                throw new InvalidDataException("YSCM file is too short.");

            if (Encoding.ASCII.GetString(data, 0, 4) != "YSCM")
                throw new InvalidDataException("Invalid YSCM signature in ysc.ybn.");

            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            stream.Position = 4;
            uint version = reader.ReadUInt32();
            uint commandCount = reader.ReadUInt32();
            uint headerUnknown = reader.ReadUInt32();

            // Command IDs and attribute IDs are bytes. 256 commands are
            // representable (IDs 0..255), but a larger table is not.
            if (commandCount > byte.MaxValue + 1u)
                throw new InvalidDataException(
                    $"YSCM declares {commandCount} commands, but command IDs are one byte.");

            long errorTableOffset = data.Length - UnknownTableSize;
            var commands = new List<YurisCommandDefinition>((int)commandCount);
            var commandIds = new Dictionary<string, byte>(StringComparer.Ordinal);

            for (int commandIndex = 0; commandIndex < (int)commandCount; commandIndex++)
            {
                string commandName = ReadZeroTerminatedSjisString(
                    reader, errorTableOffset, $"command {commandIndex} name");

                EnsureRemaining(stream, errorTableOffset, 1,
                    $"command {commandIndex} argument count");

                byte argumentCount = reader.ReadByte();
                var arguments = new List<YurisCommandArgument>(argumentCount);
                var argumentIds = new Dictionary<string, byte>(StringComparer.Ordinal);

                for (int argumentIndex = 0; argumentIndex < argumentCount; argumentIndex++)
                {
                    string argumentName = ReadZeroTerminatedSjisString(
                        reader,
                        errorTableOffset,
                        $"argument {argumentIndex} of command '{commandName}'");

                    EnsureRemaining(stream, errorTableOffset, 2,
                        $"metadata bytes for argument '{argumentName}'");

                    // These two bytes are part of the YSCM argument record.
                    // The previous parser skipped them, losing type/flag data.
                    byte value0 = reader.ReadByte();
                    byte value1 = reader.ReadByte();
                    byte argumentId = checked((byte)argumentIndex);

                    arguments.Add(new YurisCommandArgument(
                        argumentId, argumentName, value0, value1));

                    // Keep the first mapping if a file happens to repeat a
                    // name; the complete ordered list still preserves all entries.
                    argumentIds.TryAdd(argumentName, argumentId);
                }

                byte commandId = checked((byte)commandIndex);
                commands.Add(new YurisCommandDefinition(
                    commandId, commandName, arguments.AsReadOnly(), argumentIds));

                commandIds.TryAdd(commandName, commandId);
            }

            // RxYuris documents 0x25 error messages for its V5 sample, while
            // Notes.txt documents 0x90. The count is implicit in the file
            // layout; parse terminated strings until the final 0x100-byte
            // opaque table instead of hard-coding either count.
            var errorMessages = new List<string>();
            while (stream.Position < errorTableOffset)
            {
                errorMessages.Add(ReadZeroTerminatedSjisString(
                    reader, errorTableOffset, "YSCM error message"));
            }

            if (stream.Position != errorTableOffset)
                throw new InvalidDataException("YSCM error-message table has an invalid size.");

            byte[] unknownTable = reader.ReadBytes(UnknownTableSize);
            if (unknownTable.Length != UnknownTableSize || stream.Position != stream.Length)
                throw new InvalidDataException("YSCM trailing table is truncated.");

            // Publish the parsed state only after the entire file has passed
            // validation, so a failed reload cannot leave partial metadata.
            Version = version;
            HeaderUnknown = headerUnknown;
            _commands = commands.AsReadOnly();
            _errorMessages = errorMessages.AsReadOnly();
            _unknownTable = unknownTable;

            _commandIds.Clear();
            foreach (KeyValuePair<string, byte> pair in commandIds)
                _commandIds.Add(pair.Key, pair.Value);
        }

        public byte GetCommandId(string name)
        {
            if (!_commandIds.TryGetValue(name, out byte commandId))
                throw new KeyNotFoundException($"YSCM command '{name}' was not found.");

            return commandId;
        }

        public bool TryGetCommandId(string name, out byte commandId)
        {
            return _commandIds.TryGetValue(name, out commandId);
        }

        public string? GetCommandName(byte commandId)
        {
            return commandId < _commands.Count ? _commands[commandId].Name : null;
        }

        public YurisCommandDefinition GetCommand(string name)
        {
            return _commands[GetCommandId(name)];
        }

        public YurisCommandDefinition? GetCommand(byte commandId)
        {
            return commandId < _commands.Count ? _commands[commandId] : null;
        }

        public byte GetAttributeId(string commandName, string attributeName)
        {
            return GetCommand(commandName).GetArgumentId(attributeName);
        }

        public string? GetAttributeName(string commandName, byte attributeId)
        {
            IReadOnlyList<YurisCommandArgument> arguments = GetCommand(commandName).Arguments;
            return attributeId < arguments.Count ? arguments[attributeId].Name : null;
        }

        private static string ReadZeroTerminatedSjisString(
            BinaryReader reader, long sectionEnd, string fieldName)
        {
            var bytes = new List<byte>();

            while (reader.BaseStream.Position < sectionEnd)
            {
                byte value = reader.ReadByte();
                if (value == 0)
                    return StringUtil.SjisEncoding.GetString(bytes.ToArray());

                bytes.Add(value);
            }

            throw new InvalidDataException($"Unterminated Shift-JIS string in {fieldName}.");
        }

        private static void EnsureRemaining(
            Stream stream, long sectionEnd, long requiredBytes, string fieldName)
        {
            if (requiredBytes < 0 || stream.Position > sectionEnd - requiredBytes)
                throw new InvalidDataException($"YSCM is truncated while reading {fieldName}.");
        }
    }

    /// <summary>
    /// One entry in the YSCM command table.
    /// </summary>
    public sealed class YurisCommandDefinition
    {
        private readonly Dictionary<string, byte> _argumentIds;

        public byte Id { get; }
        public string Name { get; }
        public IReadOnlyList<YurisCommandArgument> Arguments { get; }

        internal YurisCommandDefinition(
            byte id,
            string name,
            IReadOnlyList<YurisCommandArgument> arguments,
            Dictionary<string, byte> argumentIds)
        {
            Id = id;
            Name = name;
            Arguments = arguments;
            _argumentIds = argumentIds;
        }

        public byte GetArgumentId(string name)
        {
            if (!_argumentIds.TryGetValue(name, out byte argumentId))
                throw new KeyNotFoundException(
                    $"YSCM argument '{name}' was not found in command '{Name}'.");

            return argumentId;
        }

        public bool TryGetArgumentId(string name, out byte argumentId)
        {
            return _argumentIds.TryGetValue(name, out argumentId);
        }
    }

    /// <summary>
    /// One named argument in a YSCM command. Value0 and Value1 are the two
    /// metadata bytes stored immediately after the zero-terminated name.
    /// </summary>
    public sealed class YurisCommandArgument
    {
        public byte Id { get; }
        public string Name { get; }
        public byte Value0 { get; }
        public byte Value1 { get; }

        internal YurisCommandArgument(byte id, string name, byte value0, byte value1)
        {
            Id = id;
            Name = name;
            Value0 = value0;
            Value1 = value1;
        }
    }
}
