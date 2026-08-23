using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts
{
    public class ExcelScript : IScript, ILineStatistics
    {
        private const string EmptyTextMarker = "(empty)";

        private readonly ExcelScriptCollection _collection;
        private readonly IXLWorkbook _workbook;
        private IXLWorksheet? _sheet;

        public ExcelScript(ExcelScriptCollection collection, IXLWorkbook workbook)
        {
            _collection = collection;
            _workbook = workbook;
        }

        public string? Extension
        {
            get { return null; }
        }

        public void Load(ScriptLocation location)
        {
            if (location.Collection != _collection)
                throw new InvalidOperationException();

            _sheet = _workbook.Worksheet(location.ScriptName);
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            if (_sheet == null)
            {
                throw new Exception("_sheet is null");
            }

            foreach (IXLRow row in _sheet.RowsUsed())
            {
                if (row.RowNumber() == 1)
                    continue;

                string? characterNames = StringUtil.NullIfEmpty(GetCellValue(row, ExcelColumn.TranslatedCharacter)) ??
                                        StringUtil.NullIfEmpty(GetCellValue(row, ExcelColumn.OriginalCharacter));
                if (characterNames != null)
                {
                    foreach (string characterName in SplitNames(characterNames))
                    {
                        yield return new ScriptString(characterName, ScriptStringType.CharacterName);
                    }
                }

                var text = GetText(row);
                if (text != null)
                {
                    text = Regex.Replace(text, @"(?<!\r)\n", "\r\n");
                    yield return new ScriptString(text, ScriptStringType.Message);
                }
            }
        }

        private static string GetCellValue(IXLRow row, ExcelColumn column)
        {
            return row.Cell((int)column + 1).GetString();
        }

        private string? GetText(IXLRow row)
        {
            var originalText = StringUtil.NullIfEmpty(GetCellValue(row, ExcelColumn.OriginalLine));
            if (originalText != null)
                Total++;

            var translatedText = StringUtil.NullIfEmpty(GetCellValue(row, ExcelColumn.TranslatedLine));
            if (translatedText != null)
                Translated++;

            var checkedText = StringUtil.NullIfEmpty(GetCellValue(row, ExcelColumn.CheckedLine));
            if (checkedText != null)
                Checked++;

            var editedText = StringUtil.NullIfEmpty(GetCellValue(row, ExcelColumn.EditedLine));
            if (editedText != null)
                Edited++;

            var text = StringUtil.NullIf(editedText, ".") ??
                          StringUtil.NullIf(checkedText, ".") ??
                          translatedText ??
                          originalText;
            return text != EmptyTextMarker ? text : string.Empty;
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            _sheet = _workbook.Worksheet(location.ScriptName);

            int rowNum = 2;
            List<string> pendingCharacterNames = new List<string>();
            foreach (ScriptString str in strings)
            {
                if (str.Type == ScriptStringType.CharacterName)
                {
                    pendingCharacterNames.Add(str.Text);
                }
                else
                {
                    IXLRow row = _sheet.Row(rowNum);
                    FillRow(row, pendingCharacterNames, str.Text);
                    pendingCharacterNames.Clear();
                    rowNum++;
                }
            }
        }

        private void FillRow(IXLRow row, List<string> characterNames, string message)
        {
            if (characterNames.Count > 0)
                FillCell(row, ExcelColumn.OriginalCharacter, JoinNames(characterNames));

            FillCell(row, ExcelColumn.OriginalLine, message.Length > 0 ? message : EmptyTextMarker);

            if (characterNames.Count > 0)
            {
                string translatedNames = JoinNames(characterNames.Select(CharacterNames.GetTranslation));
                FillCell(row, ExcelColumn.TranslatedCharacter, translatedNames);
            }
        }

        private void FillCell(IXLRow row, ExcelColumn column, string value)
        {
            IXLCell cell = row.Cell((int)column + 1);
            cell.Value = value;
            if (_sheet != null)
                cell.Style = _sheet.Column((int)column + 1).Style;
        }

        public int Translated
        {
            get;
            private set;
        }

        public int Checked
        {
            get;
            private set;
        }

        public int Edited
        {
            get;
            private set;
        }

        public int Total
        {
            get;
            set;
        }

        public void Reset()
        {
            Translated = 0;
            Checked = 0;
            Edited = 0;
            Total = 0;
        }

        private static string JoinNames(IEnumerable<string> names)
        {
            return string.Join("/", names.Select(QuoteName));
        }

        private IEnumerable<string> SplitNames(string names)
        {
            return Regex.Matches(names, @"(?:""(?:\\.|[^""])+""|[^/]+)")
                        .Cast<Match>()
                        .Select(m => UnquoteName(m.Value));
        }

        private static string QuoteName(string name)
        {
            if (!name.Contains("/") && !name.Contains("\""))
                return name;

            name = name.Replace("\\", "\\\\");
            name = name.Replace("\"", "\\\"");
            return "\"" + name + "\"";
        }

        private static string UnquoteName(string name)
        {
            if (!name.StartsWith("\"") || !name.EndsWith("\""))
                return name;

            name = name.Substring(1, name.Length - 2);
            name = Regex.Replace(name, @"\\(.)", "$1");
            return name;
        }

    }
}
