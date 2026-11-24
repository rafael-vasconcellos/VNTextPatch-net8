using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Reads the YU-RIS script list (YSTL), which maps script sequence numbers
    /// to their original relative paths and compiler-generated counts.
    ///
    /// The reference implementation switches from the old 16-byte metadata
    /// tail to the new 20-byte tail at version 474. Because the technical
    /// document describes the text-count field as starting at 477, both layouts
    /// are fully validated and the alternate layout is tried as a fallback.
    /// </summary>
    public sealed class YurisScriptList : IScript
    {
        private const int HeaderSize = 12;
        private const uint MinimumSupportedVersion = 200;
        private const uint MaximumSupportedVersionExclusive = 600;
        private const uint V5LayoutVersion = 474;
        private const int V2TrailingMetadataSize = 16;
        private const int V5TrailingMetadataSize = 20;
        private const int MinimumRecordSize = 24;

        public string Extension => ".ybn";

        public uint Version { get; private set; }

        public bool UsesV5Layout { get; private set; }

        public IReadOnlyList<YurisScriptListEntry> Entries { get; private set; }
            = Array.Empty<YurisScriptListEntry>();

        public void Load(ScriptLocation location)
        {
            Parse(File.ReadAllBytes(location.ToFilePath()));
        }

        /// <summary>
        /// Parses a complete YSTL file from memory.
        /// </summary>
        public void Parse(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length < HeaderSize)
                throw new InvalidDataException("YSTL header is truncated.");

            if (!data.AsSpan(0, 4).SequenceEqual("YSTL"u8))
                throw new InvalidDataException("Invalid YSTL signature.");

            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            stream.Position = 4;
            uint version = reader.ReadUInt32();
            uint entryCount = reader.ReadUInt32();

            if (version < MinimumSupportedVersion ||
                version >= MaximumSupportedVersionExclusive)
            {
                throw new InvalidDataException(
                    $"Unsupported YSTL version: {version}. " +
                    $"Supported range: {MinimumSupportedVersion}-{MaximumSupportedVersionExclusive - 1}.");
            }

            // The smallest valid entry has an 8-byte entry header, an empty
            // path, an 8-byte FILETIME and two signed 32-bit counters.
            if (entryCount > (uint)((data.Length - HeaderSize) / MinimumRecordSize))
            {
                throw new InvalidDataException(
                    $"YSTL declares an impossible entry count ({entryCount}).");
            }

            bool preferredV5 = version >= V5LayoutVersion;
            bool usesV5 = preferredV5;

            if (!TryParseEntries(
                data, entryCount, preferredV5,
                out List<YurisScriptListEntry>? entries,
                out string? preferredError))
            {
                bool alternateV5 = !preferredV5;
                if (!TryParseEntries(
                    data, entryCount, alternateV5,
                    out entries,
                    out string? alternateError))
                {
                    string preferredName = preferredV5 ? "V5" : "V2";
                    string alternateName = alternateV5 ? "V5" : "V2";
                    throw new InvalidDataException(
                        $"Could not parse YSTL version {version}. " +
                        $"{preferredName} layout: {preferredError}. " +
                        $"{alternateName} layout: {alternateError}.");
                }

                usesV5 = alternateV5;
            }

            // Publish state only after a complete parse has succeeded.
            Version = version;
            UsesV5Layout = usesV5;
            Entries = entries!.AsReadOnly();
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            yield break;
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            // The YSTL index contains metadata, not translatable strings.
            // Do not rewrite it as part of the normal translation pipeline.
            using IEnumerator<ScriptString> enumerator = strings.GetEnumerator();
            if (enumerator.MoveNext())
                throw new InvalidDataException("YSTL does not contain translatable strings.");
        }

        private static bool TryParseEntries(
            byte[] data,
            uint entryCount,
            bool isV5,
            out List<YurisScriptListEntry>? entries,
            out string? error)
        {
            entries = null;
            error = null;

            try
            {
                using var stream = new MemoryStream(data, writable: false);
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                stream.Position = HeaderSize;

                int trailingSize = isV5
                    ? V5TrailingMetadataSize
                    : V2TrailingMetadataSize;
                var parsedEntries = new List<YurisScriptListEntry>((int)entryCount);

                for (uint i = 0; i < entryCount; i++)
                {
                    EnsureRemaining(stream, 8, $"YSTL entry {i} header");

                    uint sequence = reader.ReadUInt32();
                    uint pathLength = reader.ReadUInt32();

                    if (sequence != i)
                    {
                        throw new InvalidDataException(
                            $"YSTL entry {i} has sequence number {sequence}; expected {i}.");
                    }

                    // The path length is bounded by the bytes remaining in
                    // this file, not by an arbitrary MAX_PATH-style limit.
                    long bytesRemaining = stream.Length - stream.Position;
                    if (bytesRemaining < trailingSize ||
                        (ulong)pathLength > (ulong)(bytesRemaining - trailingSize))
                    {
                        throw new InvalidDataException(
                            $"YSTL entry {i} has an invalid path length ({pathLength}).");
                    }

                    byte[] pathBytes = reader.ReadBytes(checked((int)pathLength));
                    if (pathBytes.Length != (int)pathLength)
                        throw new EndOfStreamException();

                    string scriptPath = StringUtil.SjisEncoding.GetString(pathBytes)
                        .Replace('\\', '/');

                    // FILETIME is one unsigned 64-bit value followed by
                    // signed 32-bit compiler counters.
                    ulong fileTime = reader.ReadUInt64();
                    int variableCount = reader.ReadInt32();
                    int labelCount = reader.ReadInt32();
                    int textCount = isV5 ? reader.ReadInt32() : 0;

                    parsedEntries.Add(new YurisScriptListEntry(
                        sequence,
                        scriptPath,
                        (uint)(fileTime >> 32),
                        (uint)fileTime,
                        variableCount,
                        labelCount,
                        textCount));
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException(
                        $"YSTL has {stream.Length - stream.Position} unexpected trailing bytes.");
                }

                entries = parsedEntries;
                return true;
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is EndOfStreamException ||
                exception is OverflowException ||
                exception is ArgumentOutOfRangeException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void EnsureRemaining(Stream stream, long required, string context)
        {
            if (required < 0 || stream.Length - stream.Position < required)
                throw new InvalidDataException($"{context} is truncated.");
        }
    }

    public sealed class YurisScriptListEntry
    {
        public uint Sequence { get; }
        public string ScriptPath { get; }
        public uint LastWriteTimeHigh { get; }
        public uint LastWriteTimeLow { get; }
        public int VariableCount { get; }
        public int LabelCount { get; }
        public int TextCount { get; }

        /// <summary>
        /// The original FILETIME value, reconstructed without losing precision.
        /// </summary>
        public ulong LastWriteTimeFileTime =>
            ((ulong)LastWriteTimeHigh << 32) | LastWriteTimeLow;

        /// <summary>
        /// A negative variable count marks an index entry that has no
        /// corresponding compiled YSTB file (for example, global.yst).
        /// </summary>
        public bool HasCompiledFile => VariableCount >= 0;

        // Compatibility aliases for callers built against the earlier parser.
        public uint Unknown0 => unchecked((uint)VariableCount);
        public uint Unknown1 => unchecked((uint)LabelCount);

        public YurisScriptListEntry(
            uint sequence,
            string scriptPath,
            uint lastWriteTimeHigh,
            uint lastWriteTimeLow,
            int variableCount,
            int labelCount,
            int textCount)
        {
            Sequence = sequence;
            ScriptPath = scriptPath;
            LastWriteTimeHigh = lastWriteTimeHigh;
            LastWriteTimeLow = lastWriteTimeLow;
            VariableCount = variableCount;
            LabelCount = labelCount;
            TextCount = textCount;
        }

        [Obsolete("Use the constructor with signed variable, label and text counts.")]
        public YurisScriptListEntry(
            uint sequence,
            string scriptPath,
            uint lastWriteTimeHigh,
            uint lastWriteTimeLow,
            uint variableCount,
            uint labelCount,
            uint textCount,
            uint unknown0,
            uint unknown1)
            : this(
                sequence,
                scriptPath,
                lastWriteTimeHigh,
                lastWriteTimeLow,
                unchecked((int)unknown0),
                unchecked((int)unknown1),
                unchecked((int)textCount))
        {
        }

        public string GetCompiledFileName()
        {
            return $"yst{Sequence:D5}.ybn";
        }
    }
}
