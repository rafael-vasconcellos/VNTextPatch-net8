using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VNTextPatch.Shared
{
    public class Extracter
    {
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
                // Do not overwrite an existing metadata file: it may contain
                // user edits from an earlier extraction.
                if (!_textCollection.Exists(textScriptName))
                    _textCollection.Add(textScriptName);

                string structuredPath = textLocation.ToFilePath();
                bool needsExport = !File.Exists(structuredPath) ||
                                   new FileInfo(structuredPath).Length == 0;

                if (needsExport)
                {
                    structured.ExportStructuredData(textLocation);
                    Console.WriteLine($"{inputScriptName} -> {textScriptName}: structured metadata");
                }
                else
                {
                    Console.WriteLine($"{inputScriptName}: structured metadata already exists; keeping it");
                }

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
            Console.WriteLine($"{inputScriptName} -> {textScriptName}: {scriptLines} lines and {scriptChars} characters");
        }

        public void ExtractAll()
        {
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
                } catch (Exception ex)
                {
                    Console.Error.Write("Error when parsing "+ inputScriptName + " : " + ex.ToString());
                }

            }
        }

        public void PrintExtractionStatistics()
        {
            Console.WriteLine($"Total lines: {TotalLines}");
            Console.WriteLine($"Total characters: {TotalCharacters}");
        }
    }
}
