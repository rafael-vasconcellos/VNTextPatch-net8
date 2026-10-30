using System.Text;
using VNTextPatch.Shared.Scripts.MarkdownScript;

namespace VNTextPatch.Shared.Scripts
{
    public class MarkdownScriptCollection : IScriptDisposableCollection, IEmptyExtractionCleanup
    {
        private YamlScriptCollection _collection;
        private readonly IScript _script;

        public MarkdownScriptCollection(string filePath)
        {
            Name = filePath;
            _collection = !File.Exists(filePath)?
                new YamlScriptCollection(Path.ChangeExtension(filePath, ".yaml"))
                 : new YamlScriptCollection(ExtractYaml(filePath));
            _script = new YamlScriptAdapter(
                this,
                _collection,
                _collection.GetTemporaryScript()
            );
        }

        public async Task<VndbMarkdownBuilder> GenerateBuilderAsync(string vnId)
        {
            using var generator = new VndbMarkdownGenerator();
            VndbMarkdownBuilder markdownBuilder;
            try
            {
                markdownBuilder = await generator.GenerateBuilderAsync(vnId);
            } catch (Exception ex)
            {
                throw new Exception($"Failed to fetch VNDB data: {ex.Message}");
            }

            return markdownBuilder;
        }

        public void Dispose()
        {
            string yamlString = _collection.GetYamlString();
            var markdownBuilder = GenerateBuilderAsync(Path.GetFileNameWithoutExtension(Name))
                .GetAwaiter().GetResult();
            markdownBuilder.Heading(2, "Game lines");
            markdownBuilder.Block("yaml", yamlString);
            VndbMarkdownGenerator.CreateFileAsync(markdownBuilder.ToString(), Name).Wait();
        }

        public static TextReader ExtractYaml(string filePath)
        {
            using var reader = new StreamReader(filePath);

            string? line;
            bool yamlStarted = false;
            var yaml = new StringBuilder();

            while ((line = reader.ReadLine()) != null)
            {
                if (!yamlStarted)
                {
                    if (line.Trim() == "```yaml")
                        yamlStarted = true;

                    continue;
                }

                if (line.Trim() == "```")
                    return new StringReader(yaml.ToString());

                yaml.AppendLine(line);
            }

            throw new InvalidDataException("The Markdown file doesn't contain a valid YAML block.");
        }

        public void CleanupEmptyExtraction()
        {
            File.Delete(Name);
        }

        public string Name
        {
            get;
        }

        public IEnumerable<string> Scripts => _collection.Scripts;

        public void Add(string scriptName) => _collection.Add(scriptName);

        public void Add(string scriptName, ScriptLocation copyFrom) => _collection.Add(scriptName, copyFrom);

        public bool Exists(string scriptName) => _collection.Exists(scriptName);



        public IScript GetTemporaryScript() => _script;

        private sealed class YamlScriptAdapter : IScript
        {
            private readonly IScriptCollection _owner;
            private readonly YamlScriptCollection _innerCollection;
            private readonly IScript _innerScript;

            public YamlScriptAdapter(
                IScriptCollection owner,
                YamlScriptCollection innerCollection,
                IScript innerScript)
            {
                _owner = owner;
                _innerCollection = innerCollection;
                _innerScript = innerScript;
            }

            public string? Extension => _innerScript.Extension;

            public void Load(ScriptLocation location)
            {
                ValidateLocation(location);

                _innerScript.Load(
                    new ScriptLocation(
                        _innerCollection,
                        location.ScriptName
                    )
                );
            }

            public IEnumerable<ScriptString> GetStrings()
            {
                return _innerScript.GetStrings();
            }

            public void WritePatched(
                IEnumerable<ScriptString> strings,
                ScriptLocation location)
            {
                ValidateLocation(location);

                _innerScript.WritePatched(
                    strings,
                    new ScriptLocation(
                        _innerCollection,
                        location.ScriptName
                    )
                );
            }

            private void ValidateLocation(ScriptLocation location)
            {
                if (!ReferenceEquals(location.Collection, _owner))
                    throw new InvalidOperationException();
            }
        }
    }
}