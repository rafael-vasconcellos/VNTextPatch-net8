using System;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VNTextPatch.Shared
{
    public class Extracter
    {
        private static readonly ILogger Logger = AppLogging.Default;
        private readonly IScriptCollection _inputCollection;
        private readonly IScriptCollection _textCollection;

        // on extraction, the input script provides the strings with `GetStrings`, that's why it needs
        // to be reloaded at each iteration
        private readonly IScript _inputScript;
        private readonly IScript _textScript;

        public Extracter(IScriptCollection inputCollection, IScriptCollection textCollection)
        {
            _inputCollection = inputCollection;
            _inputScript = inputCollection.GetTemporaryScript();

            _textCollection = textCollection;
            _textScript = _textCollection.GetTemporaryScript();
        }

        public int TotalLines
        {
            get;
            private set;
        }

        public int TotalCharacters
        {
            get;
            private set;
        }

        public void ExtractOne(string inputScriptName, string textScriptName)
        {
            if (!_inputCollection.Exists(inputScriptName))
                throw new FileNotFoundException($"{inputScriptName} does not exist in {_inputCollection.Name}");

            int scriptLines = 0;
            int scriptChars = 0;
            var inputLocation = new ScriptLocation(_inputCollection, inputScriptName);
            var textLocation = new ScriptLocation(_textCollection, textScriptName);

            _inputScript.Load(inputLocation);
            if (_inputScript is IStructuredScriptData structured &&
                structured.SupportsStructuredData)
            {
                structured.TryExportStructuredData(textLocation, inputLocation);
                return;
            }

            List<ScriptString> strings = _inputScript.GetStrings().ToList();
            if (strings.Count == 0)
                return;

            if (!_textCollection.Exists(textScriptName))
                _textCollection.Add(textScriptName);

            _textScript.WritePatched(strings, textLocation);

            foreach (ScriptString str in strings.Where(s => s.Type == ScriptStringType.Message))
            {
                scriptLines++;
                var chars = str.Text.Count(c => "「」『』【】（）“”、。？！".IndexOf(c) < 0);
                scriptChars += chars;
            }

            TotalLines += scriptLines;
            TotalCharacters += scriptChars;
            Logger.LogInformation($"{inputScriptName} -> {textScriptName}: {scriptLines} lines and {scriptChars} characters");
        }

        public void ExtractAll()
        {
            Logger.LogInformation("Extracting to local path");
            foreach (string inputScriptName in _inputCollection.Scripts)
            {

                try
                {
                    string textScriptName;
                    if (!string.IsNullOrEmpty(_inputScript.Extension))
                        textScriptName = Path.ChangeExtension(inputScriptName, _textScript.Extension);
                    else
                        textScriptName = inputScriptName + _textScript.Extension;

                    ExtractOne(inputScriptName, textScriptName);
                }
                catch (Exception ex)
                {
                    Console.Error.Write("Error when parsing " + inputScriptName + " : " + ex);
                }
            }

            // Engine-specific project files are exported through an optional
            // collection capability. The extractor remains unaware of YU-RIS.
            if (_inputCollection is IScriptCollectionProjectExporter projectExporter)
            {
                projectExporter.ExportProjectFiles(_textCollection.Name);
            }
        }

        public void PrintExtractionStatistics()
        {
            Logger.LogInformation($"Total lines: {TotalLines}");
            Logger.LogInformation($"Total characters: {TotalCharacters}");
        }
    }
}
