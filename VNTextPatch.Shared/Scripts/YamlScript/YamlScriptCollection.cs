using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;


namespace VNTextPatch.Shared.Scripts
{
    public class YamlScriptCollection : IScriptDisposableCollection, IEmptyExtractionCleanup
    {
        private readonly Dictionary<string, List<YamlEntry>> _scripts = new Dictionary<string, List<YamlEntry>>();
        private readonly List<string> _scriptNames = new List<string>();
        private YamlScript _script;
        private bool _isDirty;

        public YamlScriptCollection(string filePath)
        {
            Name = filePath;

            if (File.Exists(filePath))
                Read();

            _script = new YamlScript(this);
        }

        public string Name
        {
            get;
        }

        public IScript GetTemporaryScript()
        {
            return _script;
        }

        public IEnumerable<string> Scripts
        {
            get { return _scriptNames.ToArray(); }
        }

        public bool Exists(string scriptName)
        {
            return _scripts.ContainsKey(scriptName);
        }

        public void Add(string scriptName)
        {
            SetEntries(scriptName, new List<YamlEntry>());
        }

        public void Add(string scriptName, ScriptLocation copyFrom)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return Name;
        }

        public void Dispose()
        {
            _script = null!;

            // Só regrava se algo mudou: ler/aplicar o patch não reformata o arquivo
            // (e não descarta os comentários que alguém tenha escrito nele).
            if (_isDirty)
            {
                Save();
                _isDirty = false;
            }
        }

        public void CleanupEmptyExtraction()
        {
            File.Delete(Name);
        }

        internal List<YamlEntry> GetEntries(string scriptName)
        {
            return _scripts[scriptName];
        }

        internal void SetEntries(string scriptName, List<YamlEntry> entries)
        {
            if (!_scripts.ContainsKey(scriptName))
                _scriptNames.Add(scriptName);

            _scripts[scriptName] = entries;
            _isDirty = true;
        }

        private void Read()
        {
            YamlStream yaml = new YamlStream();
            try
            {
                using (StreamReader reader = new StreamReader(Name))
                {
                    yaml.Load(reader);
                }
            }
            catch (YamlException ex)
            {
                throw new InvalidDataException(Name + ": linha " + ex.Start.Line + ": " + ex.Message, ex);
            }

            if (yaml.Documents.Count == 0)
                return;

            if (yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw Error(yaml.Documents[0].RootNode, "a raiz do arquivo precisa ser um mapa (nome do script -> lista de falas)");

            foreach (KeyValuePair<YamlNode, YamlNode> script in root.Children)
            {
                string scriptName = ReadText(script.Key);
                List<YamlEntry> entries = new List<YamlEntry>();

                if (script.Value is YamlSequenceNode sequence)
                {
                    foreach (YamlNode item in sequence.Children)
                    {
                        entries.Add(ReadEntry(item));
                    }
                }
                else if (!(script.Value is YamlScalarNode { Value: null or "" }))
                {
                    throw Error(script.Value, "o conteúdo de um script precisa ser uma lista de falas");
                }

                _scriptNames.Add(scriptName);
                _scripts[scriptName] = entries;
            }
        }

        private YamlEntry ReadEntry(YamlNode node)
        {
            if (node is not YamlMappingNode map)
                throw Error(node, "cada fala precisa ser um mapa com 'name' e/ou 'message'");

            YamlEntry entry = new YamlEntry();
            foreach (KeyValuePair<YamlNode, YamlNode> field in map.Children)
            {
                string key = ReadText(field.Key);
                switch (key)
                {
                    case "name":
                        entry.Name = ReadText(field.Value);
                        break;

                    case "names":
                        if (field.Value is not YamlSequenceNode names)
                            throw Error(field.Value, "'names' precisa ser uma lista");
                        entry.Names = new List<string>();
                        foreach (YamlNode name in names.Children)
                        {
                            entry.Names.Add(ReadText(name));
                        }
                        break;

                    case "message":
                        entry.Message = ReadText(field.Value);
                        break;

                    default:
                        throw Error(field.Key, "campo desconhecido '" + key + "'");
                }
            }

            if (entry.Message == null)
                throw Error(map, "falta o campo 'message'");

            return entry;
        }

        private string ReadText(YamlNode node)
        {
            if (node is YamlScalarNode scalar)
                return scalar.Value ?? string.Empty;

            throw Error(node, "era esperado um texto");
        }

        private InvalidDataException Error(YamlNode node, string message)
        {
            return new InvalidDataException(Name + ": linha " + node.Start.Line + ": " + message);
        }

        private void Save()
        {
            YamlMappingNode root = new YamlMappingNode();
            foreach (string scriptName in _scriptNames)
            {
                YamlSequenceNode entries = new YamlSequenceNode();
                foreach (YamlEntry entry in _scripts[scriptName])
                {
                    entries.Add(CreateEntryNode(entry));
                }
                root.Add(CreateNameNode(scriptName), entries);
            }

            string tempPath = Name + ".temp";
            using (StreamWriter writer = new StreamWriter(tempPath, false, new UTF8Encoding(false)))
            {
                EmitterSettings settings = EmitterSettings.Default
                                                          .WithIndentedSequences()
                                                          .WithBestWidth(int.MaxValue)
                                                          .WithNewLine("\n");
                new YamlStream(new YamlDocument(root)).Save(new Emitter(writer, settings), false);
            }
            File.Move(tempPath, Name, true);
        }

        private static YamlMappingNode CreateEntryNode(YamlEntry entry)
        {
            YamlMappingNode node = new YamlMappingNode();

            if (entry.Names != null)
            {
                YamlSequenceNode names = new YamlSequenceNode();
                foreach (string name in entry.Names)
                {
                    names.Add(CreateNameNode(name));
                }
                node.Add("names", names);
            }
            else if (!string.IsNullOrEmpty(entry.Name))
            {
                node.Add("name", CreateNameNode(entry.Name));
            }

            node.Add("message", CreateMessageNode(entry.Message ?? string.Empty));
            return node;
        }

        private static YamlScalarNode CreateNameNode(string name)
        {
            // Sem aspas, como no template, a não ser que outro leitor de YAML pudesse
            // interpretar o texto como número, booleano ou nulo.
            return new YamlScalarNode(name)
                   {
                       Style = IsAmbiguousPlainScalar(name) ? ScalarStyle.DoubleQuoted : ScalarStyle.Any
                   };
        }

        private static YamlScalarNode CreateMessageNode(string text)
        {
            if (CanUseLiteralStyle(text))
            {
                return new YamlScalarNode(text.Replace("\r\n", "\n"))
                       {
                           Style = ScalarStyle.Literal
                       };
            }

            return new YamlScalarNode(text)
                   {
                       Style = ScalarStyle.DoubleQuoted
                   };
        }

        private static bool CanUseLiteralStyle(string text)
        {
            // Bloco literal (|) só para texto com várias linhas que o emitter consiga gravar sem perda.
            // Qualquer outro caso fica entre aspas duplas, que preserva o texto exatamente.
            if (!text.Contains('\n') || string.IsNullOrWhiteSpace(text) || char.IsWhiteSpace(text[0]))
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
                    return false;

                if (c != '\r' && c != '\n' && c != '\t' && (char.IsControl(c) || c == '\u2028' || c == '\u2029'))
                    return false;
            }
            return true;
        }

        private static bool IsAmbiguousPlainScalar(string text)
        {
            switch (text.ToLowerInvariant())
            {
                case "":
                case "~":
                case "null":
                case "true":
                case "false":
                case "yes":
                case "no":
                case "on":
                case "off":
                    return true;
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }
    }
}