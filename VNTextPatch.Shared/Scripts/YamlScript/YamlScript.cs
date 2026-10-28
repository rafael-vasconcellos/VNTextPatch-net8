using System;
using System.Collections.Generic;


namespace VNTextPatch.Shared.Scripts
{
    public class YamlReadOnlyScript : IReadOnlyScript
    {
        protected readonly YamlScriptCollection _collection;
        private protected List<YamlEntry> _entries = new List<YamlEntry>();

        public YamlReadOnlyScript(YamlScriptCollection collection)
        {
            _collection = collection;
        }

        public YamlReadOnlyScript(ScriptLocation location)
        {
            _collection = (YamlScriptCollection)location.Collection;
            _entries = _collection.GetEntries(location.ScriptName);
        }

        public string? Extension
        {
            get { return null; }
        }

        public virtual void Load(ScriptLocation location)
        {
            if (location.Collection is not YamlScriptCollection collection)
                throw new InvalidOperationException();

            List<YamlEntry> entries = collection.GetEntries(location.ScriptName);
            _entries = entries;
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            foreach (YamlEntry entry in _entries)
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

                // Mesma normalização do ExcelScript: quebras de linha sempre como \r\n
                string text = (entry.Message ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
                yield return new ScriptString(text, ScriptStringType.Message);
            }
        }
    }

    public class YamlScript : YamlReadOnlyScript, IScript
    {
        public YamlScript(YamlScriptCollection collection) : base(collection) {}

        public override void Load(ScriptLocation location)
        {
            if (location.Collection != _collection)
                throw new InvalidOperationException();

            _entries = _collection.GetEntries(location.ScriptName);
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            if (location.Collection != _collection)
                throw new InvalidOperationException();

            List<YamlEntry> entries = new List<YamlEntry>();
            YamlEntry? pendingEntry = null;
            int index = 1;
            foreach (ScriptString str in strings)
            {
                if (str.Type == ScriptStringType.CharacterName)
                {
                    if (pendingEntry == null)
                    {
                        pendingEntry = new YamlEntry { Name = str.Text };
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
                        pendingEntry.Index = index++;
                        entries.Add(pendingEntry);
                        pendingEntry = null;
                    }
                    else
                    {
                        YamlEntry yamlEntry = new YamlEntry { 
                            Message = str.Text,
                            Index = index++
                        };
                        entries.Add(yamlEntry);
                    }
                }
            }

            var existingFile = YamlScriptCollection.Load(location.ToFilePath())?.TryGetEntries(location.ScriptName);
            var newEntries = existingFile != null?
                entries.Select((entry, i) => i<existingFile.Count? existingFile[i] : entry).ToList()
                : entries;
            _collection.SetEntries(location.ScriptName, newEntries);
        }
    }

    internal class YamlEntry
    {
        public int? Index
        {
            get;
            set;
        }
        public string? Name
        {
            get;
            set;
        }

        public List<string>? Names
        {
            get;
            set;
        }

        public string? Message
        {
            get;
            set;
        }
    }
}