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
    /// YSTL is metadata rather than translatable script content, so this class
    /// exposes its entries but does not produce translation strings.
    /// </summary>
    public sealed class YurisScriptList : IScript
    {
        private const int HeaderSize = 12;
        private const int MaxPathLength = 256;

        public string Extension => ".ybn";

        public uint Version { get; private set; }

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
            Version = reader.ReadUInt32();
            uint entryCount = reader.ReadUInt32();

            // YU-RIS uses the older variable-sized V2 records before version 3.00.
            // V5 records add VariableCount, LabelCount and TextCount.
            bool isV5 = Version >= 300;
            var entries = new List<YurisScriptListEntry>(
                entryCount <= int.MaxValue ? (int)entryCount : 0);

            for (uint i = 0; i < entryCount; i++)
            {
                EnsureRemaining(stream, 8, "YSTL entry header");

                uint sequence = reader.ReadUInt32();
                uint pathLength = reader.ReadUInt32();

                if (pathLength > MaxPathLength)
                    throw new InvalidDataException($"YSTL entry {i} has an invalid path length ({pathLength}).");

                uint trailingSize = isV5 ? 20u : 16u;
                EnsureRemaining(stream, checked((long)pathLength + trailingSize), "YSTL entry");

                byte[] pathBytes = reader.ReadBytes((int)pathLength);
                if (pathBytes.Length != pathLength)
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

                entries.Add(new YurisScriptListEntry(
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
                throw new InvalidDataException("YSTL contains unexpected trailing data.");

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
