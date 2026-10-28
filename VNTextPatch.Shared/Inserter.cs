using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VNTextPatch.Shared
{
    public class Inserter
    {
        private readonly IScriptCollection _inputCollection;
        private readonly IScriptCollection _textCollection;
        private readonly IScriptCollection _outputCollection;

        private readonly IScript _textScript;
        private readonly IScript _inputOutputScript; // input and output use the same script (ybn, ks, etc.)

        private int Translated = 0;
        private int Total = 0;

        public Inserter(IScriptCollection inputCollection, IScriptCollection textCollection, IScriptCollection outputCollection)
        {
            _inputCollection = inputCollection;
            _textCollection = textCollection;
            _outputCollection = outputCollection;

            if (inputCollection.GetType() != outputCollection.GetType())
                throw new ArgumentException("Input and output collections must have the same type");

            _inputOutputScript = inputCollection.GetTemporaryScript();
            _textScript = textCollection.GetTemporaryScript();
        }

        private ISheetStatistics? Statistics
        {
            get { return _textScript as ISheetStatistics; }
        }

        public void PrintStatistics()
        {
            var statistics = Statistics;
            switch (statistics)
            {
                case ISheetStatistics sheetStatistics:
                    Console.WriteLine($"Total lines: {sheetStatistics.Total}");
                    Console.WriteLine($"Translated:  {sheetStatistics.Translated,-10} ({(float)sheetStatistics.Translated / sheetStatistics.Total:P2})");
                    Console.WriteLine($"Checked:     {sheetStatistics.Checked,-10} ({(float)sheetStatistics.Checked / sheetStatistics.Total:P2})");
                    Console.WriteLine($"Edited:      {sheetStatistics.Edited,-10} ({(float)sheetStatistics.Edited / sheetStatistics.Total:P2})");
                    break;

                default:
                    Console.WriteLine($"Translated:  {Translated,-10}");
                    Console.WriteLine($"Total lines: {Total}");
                    Console.WriteLine($"Rate: {(float)Translated / Total:P2}");
                    break;
            }
        }

        public void InsertOne(string inputScriptName, string textScriptName, string outputScriptName)
        {
            if (!_inputCollection.Exists(inputScriptName))
                throw new FileNotFoundException($"{inputScriptName} does not exist in {_inputCollection.Name}");

            if (!_textCollection.Exists(textScriptName))
                throw new FileNotFoundException($"{textScriptName} does not exist in {_textCollection.Name}");

            _inputOutputScript.Load(new ScriptLocation(_inputCollection, inputScriptName));
            _textScript.Load(new ScriptLocation(_textCollection, textScriptName));
            List<ScriptString> strings = _textScript.GetStrings().ToList();
            int inputCount = _inputCollection.GetMessageStrings(inputScriptName).Count;
            int textCount = strings.Count(s => s.Type == ScriptStringType.Message);
            Console.WriteLine($"{inputScriptName}: {(float)textCount / inputCount:P2} ({textCount})");
            Translated += textCount; Total += inputCount;

            _outputCollection.Add(outputScriptName);
            _inputOutputScript.WritePatched(strings, new ScriptLocation(_outputCollection, outputScriptName));
        }

        public void InsertAll()
        {
            foreach (string inputScriptName in _inputCollection.Scripts)
            {
                string textScriptName;
                if (!string.IsNullOrEmpty(_inputOutputScript.Extension))
                    textScriptName = Path.ChangeExtension(inputScriptName, _textScript.Extension);
                else
                    textScriptName = inputScriptName + _textScript.Extension;

                if (_textCollection.Exists(textScriptName))
                {
                    InsertOne(inputScriptName, textScriptName, inputScriptName);
                }
                else
                {
                    _outputCollection.Add(inputScriptName, new ScriptLocation(_inputCollection, inputScriptName));
                    AddInputScriptMessageCount(inputScriptName);
                }
            }
        }

        private void AddInputScriptMessageCount(string scriptName)
        {
            _inputOutputScript.Load(new ScriptLocation(_inputCollection, scriptName));
            var total = _inputOutputScript.GetStrings().Count(s => s.Type == ScriptStringType.Message);
            Statistics?.Total += total;
            Total += total;
        }
    }
}
