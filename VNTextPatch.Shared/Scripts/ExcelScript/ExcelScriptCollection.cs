using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ClosedXML.Excel;

namespace VNTextPatch.Shared.Scripts
{
    public class ExcelScriptCollection : IScriptDisposableCollection, IEmptyExtractionCleanup
    {
        private XLWorkbook _workbook;
        private ExcelScript _script;
        private bool _isEmpty;

        public ExcelScriptCollection(string filePath)
        {
            Name = filePath;

            if (!File.Exists(filePath))
            {
                var folderPath = AppContext.BaseDirectory;
                string templateFilePath = Path.Combine(folderPath, "template.xlsx");
                _isEmpty = true;
                if (File.Exists(templateFilePath))
                    File.Copy(templateFilePath, filePath);
                else
                    CreateFile(filePath);
            }

            _workbook = new XLWorkbook(filePath);
            _script = new ExcelScript(this, _workbook);
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
            get { return _workbook.Worksheets.Select(ws => ws.Name); }
        }

        public bool Exists(string scriptName)
        {
            return _workbook.Worksheets.TryGetWorksheet(scriptName, out _);
        }

        public void Add(string scriptName)
        {
            if (_isEmpty)
            {
                _workbook.Worksheet(1).Name = scriptName;
                _isEmpty = false;
            }
            else
            {
                IXLWorksheet sheet = _workbook.Worksheet(1).CopyTo(scriptName);
                int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                for (int i = lastRow; i > 1; i--)
                {
                    sheet.Row(i).Delete();
                }
            }
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

            if (_workbook != null)
            {
                using (Stream stream = File.Open(Name + ".temp", FileMode.Create))
                {
                    _workbook.SaveAs(stream);
                }
                _workbook.Dispose();
                File.Delete(Name);
                File.Move(Name + ".temp", Name);
            }
        }

        public void CleanupEmptyExtraction()
        {
            File.Delete(Name);
        }

        private static void CreateFile(string filePath)
        {
            using var stream = File.Create(filePath);
            using var templateStream = Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream("VNTextPatch.Shared.template.xlsx");

            using var workbook = templateStream is not null
                ? new XLWorkbook(templateStream)
                : new XLWorkbook();

            if (templateStream is null)
                workbook.AddWorksheet("Script");

            workbook.SaveAs(stream);
        }

    }
}
