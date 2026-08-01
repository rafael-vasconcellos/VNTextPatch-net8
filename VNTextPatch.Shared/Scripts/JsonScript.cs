using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using VNTextPatch.Shared.Util;


namespace VNTextPatch.Shared.Scripts
{
    public partial class JsonScript : IScript
    {
        private static readonly JsonContext Context = new(new JsonSerializerOptions(JsonContext.Default.Options)
        {
            Encoder = MinimalJsonEncoder.Instance
        });

        public string Extension => ".json";

        private Entry[] _entries = [];

        public void Load(ScriptLocation location)
        {
            using FileStream stream = File.OpenRead(location.ToFilePath());
            _entries = JsonSerializer.Deserialize(stream, Context.EntryArray) ?? _entries;
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            foreach (Entry entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.Name))
                {
                    yield return new ScriptString(entry.Name, ScriptStringType.CharacterName);
                }
                else if (entry.Names != null)
                {
                    foreach (string name in entry.Names)
                    {
                        yield return new ScriptString(name, ScriptStringType.CharacterName);
                    }
                }

                yield return new ScriptString(entry.Message!, ScriptStringType.Message);
            }
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            List<Entry> entries = new List<Entry>();
            Entry? pendingEntry = null;
            foreach (ScriptString str in strings)
            {
                if (str.Type == ScriptStringType.CharacterName)
                {
                    if (pendingEntry == null)
                    {
                        pendingEntry = new Entry { Name = str.Text };
                    }
                    else
                    {
                        if (pendingEntry.Names == null)
                        {
                            pendingEntry.Names = new List<string> { pendingEntry.Name! };
                            pendingEntry.Name = null;
                        }
                        pendingEntry.Names.Add(str.Text);
                    }
                }
                else
                {
                    if (pendingEntry != null)
                    {
                        pendingEntry.Message = str.Text;
                        entries.Add(pendingEntry);
                        pendingEntry = null;
                    }
                    else
                    {
                        entries.Add(new Entry { Message = str.Text });
                    }
                }
            }

            using Stream stream = File.Open(location.ToFilePath(), FileMode.Create);
            JsonSerializer.Serialize(stream, entries, Context.ListEntry);
        }

        private class Entry
        {
            [JsonPropertyName("name")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Name
            {
                get;
                set;
            }

            [JsonPropertyName("names")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<string>? Names
            {
                get;
                set;
            }

            [JsonPropertyName("message")]
            public string? Message
            {
                get;
                set;
            }
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(Entry[]))]
        [JsonSerializable(typeof(List<Entry>))]
        private partial class JsonContext : JsonSerializerContext
        {
        }
    }
}
