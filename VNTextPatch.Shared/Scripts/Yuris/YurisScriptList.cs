using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    /// <summary>
    /// Reads the YU-RIS script list (YSTL), which maps script sequence numbers
    /// to their original relative paths.
    ///
    /// Versions 300 through 466 are accepted by both the V2 and V5 reference
    /// readers, so the record layout is validated against the complete file
    /// instead of being selected from the version number alone.
    /// </summary>
    public sealed class YurisScriptList : IScript
    {
        private const int HeaderSize = 12;
        private const int MaxPathLength = 256;
        private const int UnknownTableSizeV2 = 16;
        private const int MetadataSizeV5 = 20;

        public string Extension => ".ybn";

        public uint Version { get; private set; }

        public bool UsesV5Layout { get; private set; }

        public IReadOnlyList<YurisScriptListEntry> Entries { get; private set; }
            = Array.Empty<YurisScriptListEntry>();

        public void Load(ScriptLocation location)
        {
            byte[] data = File.ReadAllBytes(location.ToFilePath());

            if (data.Length < HeaderSize)
                throw new InvalidDataException("YSTL header is truncated.");

            if (Encoding.ASCII.GetString(data, 0, 4) != "YSTL")
                throw new InvalidDataException("Invalid YSTL signature.");

            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            reader.BaseStream.Position = 4;
            uint version = reader.ReadUInt32();
            uint entryCount = reader.ReadUInt32();

            // RxYuris accepts V2 through 466 and V5 from 300 onward.
            // The overlapping version range must be resolved by validating
            // each candidate record layout all the way to EOF.
            bool canBeV2 = version <= 466;
            bool canBeV5 = version >= 300;

            if (!canBeV2 && !canBeV5)
                throw new InvalidDataException($"Unsupported YSTL version: {version}.");

            // Every entry needs at least its sequence, path length and the
            // fixed-size metadata. This also prevents unreasonable list
            // allocations from a corrupt entry count.
            int minimumEntrySize = canBeV2 ? 8 + UnknownTableSizeV2 : 8 + MetadataSizeV5;
            if (entryCount > (uint)((data.Length - HeaderSize) / minimumEntrySize))
                throw new InvalidDataException(
                    $"YSTL declares an impossible entry count ({entryCount}).");

            List<YurisScriptListEntry>? entries = null;
            bool usesV5 = false;
            string? v5Error = null;
            string? v2Error = null;

            // Prefer the conventional version mapping, then try the other
            // layout only when that complete parse fails.
            if (canBeV5)
            {
                if (TryParseEntries(data, entryCount, isV5: true, out entries, out v5Error))
                {
                    usesV5 = true;
                }
            }

            if (entries == null && canBeV2)
            {
                if (TryParseEntries(data, entryCount, isV5: false, out entries, out v2Error))
                {
                    usesV5 = false;
                }
            }

            if (entries == null)
            {
                throw new InvalidDataException(
                    $"Could not parse YSTL version {version}. " +
                    $"V5 layout: {v5Error ?? "not applicable"}. " +
                    $"V2 layout: {v2Error ?? "not applicable"}.");
            }

            Version = version;
            UsesV5Layout = usesV5;
            Entries = entries.AsReadOnly();
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

                int trailingSize = isV5 ? MetadataSizeV5 : UnknownTableSizeV2;
                var parsedEntries = new List<YurisScriptListEntry>((int)entryCount);

                for (uint i = 0; i < entryCount; i++)
                {
                    EnsureRemaining(stream, 8, $"YSTL entry {i} header");

                    uint sequence = reader.ReadUInt32();
                    uint pathLength = reader.ReadUInt32();

                    if (pathLength > MaxPathLength)
                    {
                        throw new InvalidDataException(
                            $"YSTL entry {i} has an invalid path length ({pathLength}).");
                    }

                    EnsureRemaining(
                        stream,
                        checked((long)pathLength + trailingSize),
                        $"YSTL entry {i}");

                    byte[] pathBytes = reader.ReadBytes((int)pathLength);
                    if (pathBytes.Length != (int)pathLength)
                        throw new EndOfStreamException();

                    string scriptPath = StringUtil.SjisEncoding.GetString(pathBytes)
                        .Replace('\\', '/');

                    uint lastWriteTimeHigh = reader.ReadUInt32();
                    uint lastWriteTimeLow = reader.ReadUInt32();

                    uint variableCount = 0;
                    uint labelCount = 0;
                    uint textCount = 0;
                    uint unknown0 = 0;
                    uint unknown1 = 0;

                    if (isV5)
                    {
                        variableCount = reader.ReadUInt32();
                        labelCount = reader.ReadUInt32();
                        textCount = reader.ReadUInt32();
                    }
                    else
                    {
                        unknown0 = reader.ReadUInt32();
                        unknown1 = reader.ReadUInt32();
                    }

                    parsedEntries.Add(new YurisScriptListEntry(
                        sequence,
                        scriptPath,
                        lastWriteTimeHigh,
                        lastWriteTimeLow,
                        variableCount,
                        labelCount,
                        textCount,
                        unknown0,
                        unknown1));
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
        public uint VariableCount { get; }
        public uint LabelCount { get; }
        public uint TextCount { get; }
        public uint Unknown0 { get; }
        public uint Unknown1 { get; }

        public ulong LastWriteTimeFileTime =>
            ((ulong)LastWriteTimeHigh << 32) | LastWriteTimeLow;

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
        {
            Sequence = sequence;
            ScriptPath = scriptPath;
            LastWriteTimeHigh = lastWriteTimeHigh;
            LastWriteTimeLow = lastWriteTimeLow;
            VariableCount = variableCount;
            LabelCount = labelCount;
            TextCount = textCount;
            Unknown0 = unknown0;
            Unknown1 = unknown1;
        }

        public string GetCompiledFileName()
        {
            return $"yst{Sequence:D5}.ybn";
        }
    }
}
