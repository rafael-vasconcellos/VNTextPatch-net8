using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VNTextPatch.Shared.Util;


namespace VNTextPatch.Shared.Scripts
{
    public partial class JsonlScript : IScript
    {
        private static readonly JsonlScriptContext Context = new(new JsonSerializerOptions(JsonlScriptContext.Default.Options)
        {
            Encoder = MinimalJsonEncoder.Instance
        });

        public string Extension => ".jsonl";

        private Entry[] _entries = [];

        public void Load(ScriptLocation location)
        {
            var entries = new List<Entry>();
            foreach (string line in File.ReadLines(location.ToFilePath()))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Entry? entry = JsonSerializer.Deserialize(line, Context.Entry);
                if (entry != null)
                    entries.Add(entry);
            }
            _entries = entries.ToArray();
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

            using StreamWriter writer = new StreamWriter(File.Open(location.ToFilePath(), FileMode.Create));
            writer.NewLine = "\n";
            foreach (Entry entry in entries)
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, Context.Entry));
            }
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

        [JsonSerializable(typeof(Entry))]
        private partial class JsonlScriptContext : JsonSerializerContext
        {
        }
    }
}
