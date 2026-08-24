using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VNTextPatch.Shared;
using VNTextPatch.Shared.Scripts;
using VNTextPatch.Shared.Util;

namespace VNTextPatch
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Options options = Options.Parse(args, out args);
            if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
            {
                PrintUsage();
                return;
            }

            try
            {
                string operation = args[0];
                switch (operation)
                {
                    case "extractlocal":
                        ExtractLocal(args, options);
                        break;

                    case "insertlocal":
                        InsertLocal(args, options);
                        break;

                    case "insertgdocs":
                        InsertGoogleDocs(args, options);
                        break;

                    default:
                        Console.WriteLine($"Unknown operation: {operation}");
                        PrintUsage();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void ExtractLocal(string[] args, Options options)
        {
            if (args.Length != 3)
            {
                PrintUsage();
                return;
            }
            Console.WriteLine("Extracting to local path");

            string inputPath = Path.GetFullPath(args[1]);
            string textPath = Path.GetFullPath(args[2]);


            ScriptLocation inputLocation;
            if (!TryParseLocalPath(inputPath, options.Format, out inputLocation))
                return;

            if (!Directory.Exists(textPath) && IsValidDirectoryPath(textPath)) 
                Directory.CreateDirectory(textPath);

            ScriptLocation textLocation = GetLocalTextScriptLocation(inputLocation, textPath);
            Extracter extracter = new Extracter(inputLocation.Collection, textLocation.Collection);
            try
            {
                
                if (inputLocation.ScriptName != null)
                    extracter.ExtractOne(inputLocation.ScriptName, textLocation.ScriptName);
                else
                    extracter.ExtractAll();

                PrintExtractionStatistics(extracter.TotalLines, extracter.TotalCharacters);
            }
            finally
            {
                IDisposable? textCollection = textLocation.Collection as IDisposable;
                textCollection?.Dispose();

                if (extracter.TotalLines == 0)
                    (textLocation.Collection as IEmptyExtractionCleanup)?.CleanupEmptyExtraction();
            }


            CharacterNames.Save();
        }

        private static void InsertLocal(string[] args, Options options)
        {
            if (args.Length != 4 && args.Length != 5)
            {
                PrintUsage();
                return;
            }

            string inputPath = Path.GetFullPath(args[1]);
            string textPath = Path.GetFullPath(args[2]);
            string outputPath = Path.GetFullPath(args[3]);
            string? sjisExtPath = args.Length > 4 ? Path.GetFullPath(args[4]) : null;

            ScriptLocation inputLocation;
            if (!TryParseLocalPath(inputPath, options.Format, out inputLocation))
                return;

            if (!Directory.Exists(outputPath) && IsValidDirectoryPath(outputPath)) 
                Directory.CreateDirectory(outputPath);

            ScriptLocation textLocation = GetLocalTextScriptLocation(inputLocation, textPath);
            if (sjisExtPath == null)
            {
                var dirPath = inputLocation.ScriptName != null ? Path.GetDirectoryName(outputPath) : outputPath;
                sjisExtPath = Path.Combine(dirPath!, "sjis_ext.bin");
            }

            if (File.Exists(sjisExtPath))
                StringUtil.SjisTunnelEncoding.SetMappingTable(File.ReadAllBytes(sjisExtPath));

            try
            {
                Inserter inserter;
                if (inputLocation.ScriptName != null)
                {
                    ScriptLocation outputLocation = ScriptLocation.FromFilePath(outputPath, options.Format);
                    inserter = new Inserter(inputLocation.Collection, textLocation.Collection, outputLocation.Collection);
                    inserter.InsertOne(inputLocation.ScriptName, textLocation.ScriptName, outputLocation.ScriptName);
                }
                else
                {
                    FolderScriptCollection inputCollection = (FolderScriptCollection)inputLocation.Collection;
                    FolderScriptCollection outputCollection = new FolderScriptCollection(outputPath, inputCollection.Extension, options.Format);
                    inserter = new Inserter(inputCollection, textLocation.Collection, outputCollection);
                    inserter.InsertAll();
                }

                byte[] sjisExtContent = StringUtil.SjisTunnelEncoding.GetMappingTable();
                if (sjisExtContent.Length > 0)
                {
                    File.WriteAllBytes(sjisExtPath, sjisExtContent);
                }

                if (inserter.Statistics != null)
                    PrintInsertionStatistics(inserter.Statistics);
            }
            finally
            {
                IDisposable? textCollection = textLocation.Collection as IDisposable;
                textCollection?.Dispose();
            }
        }

        private static void InsertGoogleDocs(string[] args, Options options)
        {
            if (args.Length != 4 && args.Length != 5)
            {
                PrintUsage();
                return;
            }

            string inputPath = Path.GetFullPath(args[1]);
            string spreadsheetId = args[2];
            string outputPath = Path.GetFullPath(args[3]);
            string? sjisExtPath = args.Length > 4 ? Path.GetFullPath(args[4]) : null;

            ScriptLocation inputLocation;
            if (!TryParseLocalPath(inputPath, options.Format, out inputLocation))
                return;

            var textCollection = GoogleDocsScriptFactory.Build(spreadsheetId);

            if (sjisExtPath == null)
            {
                var dirPath = inputLocation.ScriptName != null ? Path.GetDirectoryName(outputPath) : outputPath;
                sjisExtPath = Path.Combine(dirPath!, "sjis_ext.bin");
            }

            if (File.Exists(sjisExtPath))
                StringUtil.SjisTunnelEncoding.SetMappingTable(File.ReadAllBytes(sjisExtPath));

            string textScriptName;

            Inserter inserter;
            if (inputLocation.ScriptName != null)
            {
                textScriptName = Path.GetFileNameWithoutExtension(inputPath);
                ScriptLocation outputLocation = ScriptLocation.FromFilePath(outputPath, options.Format);
                inserter = new Inserter(inputLocation.Collection, textCollection, outputLocation.Collection);
                inserter.InsertOne(inputLocation.ScriptName, textScriptName, outputLocation.ScriptName);
            }
            else
            {
                FolderScriptCollection inputCollection = (FolderScriptCollection)inputLocation.Collection;
                FolderScriptCollection outputCollection = new FolderScriptCollection(outputPath, inputCollection.Extension, options.Format);
                inserter = new Inserter(inputCollection, textCollection, outputCollection);
                inserter.InsertAll();
            }

            byte[] sjisExtContent = StringUtil.SjisTunnelEncoding.GetMappingTable();
            if (sjisExtContent.Length > 0)
                File.WriteAllBytes(sjisExtPath, sjisExtContent);

            if (inserter.Statistics != null)
                PrintInsertionStatistics(inserter.Statistics);
        }

        private static ScriptLocation GetLocalTextScriptLocation(ScriptLocation inputLocation, string textPath)
        {
            if (Directory.Exists(textPath))
            {
                if (inputLocation.ScriptName != null)
                    throw new ArgumentException("Input path and script path must be of the same type (file or folder).");

                string? firstFilePath = Directory.EnumerateFiles(textPath, "*", SearchOption.AllDirectories).FirstOrDefault();
                string extension = firstFilePath != null? 
                    Path.GetExtension(firstFilePath).ToLower() :
                    "." + GlobalVariables.OutputFormat;

                FolderScriptCollection collection = new FolderScriptCollection(textPath, extension);
                return new ScriptLocation(collection: collection, scriptName: null);
            }

            switch (Path.GetExtension(textPath)?.ToLower())
            {
                case ".json":
                    if (inputLocation.ScriptName == null)
                        throw new ArgumentException("Input path and script path must be of the same type (file or folder).");

                    return ScriptLocation.FromFilePath(textPath);

                case ".xlsx":
                    var collection = ExcelScriptFactory.Build(textPath);
                    string? scriptName = inputLocation.ScriptName != null ? Path.GetFileNameWithoutExtension(inputLocation.ScriptName) : null;
                    return new ScriptLocation(collection, scriptName!);

                default:
                    throw new ArgumentException("Script path must be a .json or .xlsx file or an existing folder");
            }
        }

        private static bool TryParseLocalPath(string path, string? format, out ScriptLocation location)
        {
            location = new ScriptLocation();

            if (File.Exists(path))
            {
                location = ScriptLocation.FromFilePath(path, format);
                return true;
            }

            if (Directory.Exists(path))
            {
                string? firstFilePath = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).FirstOrDefault();
                if (firstFilePath == null)
                {
                    Console.WriteLine($"Folder {path} is empty");
                    return false;
                }
                IScriptCollection collection = new FolderScriptCollection(path, Path.GetExtension(firstFilePath), format);
                location = new ScriptLocation(collection: collection, scriptName: null);
                return true;
            }

            Console.WriteLine($"File/folder {path} does not exist");
            return false;
        }

        private static bool IsValidDirectoryPath(string path)
        {
            //Console.WriteLine(path);
            string? pathExtension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (pathExtension != null && pathExtension.Length > 0)
                return false;

            try
            {
                Path.GetFullPath(path);
                return true;
            }
            catch
            {
                //Console.WriteLine(e);
                return false;
            }
        }

        private static void PrintExtractionStatistics(int lines, int characters)
        {
            Console.WriteLine($"Total lines: {lines}");
            Console.WriteLine($"Total characters: {characters}");
        }

        private static void PrintInsertionStatistics(ILineStatistics statistics)
        {
            Console.WriteLine($"Total lines: {statistics.Total}");
            Console.WriteLine($"Translated:  {statistics.Translated,-10} ({(float)statistics.Translated / statistics.Total:P2})");
            Console.WriteLine($"Checked:     {statistics.Checked,-10} ({(float)statistics.Checked / statistics.Total:P2})");
            Console.WriteLine($"Edited:      {statistics.Edited,-10} ({(float)statistics.Edited / statistics.Total:P2})");
        }

        private static void PrintUsage()
        {
            string? assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            Console.WriteLine($"VNTextPatch v. {version}");
            Console.WriteLine($"Usage:\n");

            Console.WriteLine($"{assemblyName} extractlocal <source> <scripts>");
            Console.WriteLine($"{assemblyName} insertlocal <source> <scripts> <patched>");
            Console.WriteLine($"{assemblyName} insertgdocs <source> <spreadsheetId> <patched>\n");
            Console.WriteLine($"    <source>     folder containing original game files");
            Console.WriteLine($"    <scripts>    folder to receive text files");
            Console.WriteLine($"    <patched>    folder to receive patched game files");
            Console.WriteLine($"    <spreadsheetId>    ID of a Google Docs spreadsheet\n");

            Console.WriteLine($"Excel:\n");
            Console.WriteLine($"{assemblyName} extractlocal <source> <script.xlsx>");
            Console.WriteLine($"{assemblyName} insertlocal <source> <script.xlsx> <patched>\n");

            Console.WriteLine($"Options:");
            Console.WriteLine($"    -h, --help    Displays this screen");
            Console.WriteLine($"    --output=<value>    Output format of the script text files. JSON or JSONL. Default is JSONL");
        }

        private class Options
        {
            public static Options Parse(string[] args, out string[] unnamedArgs)
            {
                Options options = new Options();
                List<string> unnamedArgsList = new List<string>();
                foreach (string arg in args)
                {
                    if (arg == "--read-utf8")
                    {
                        GlobalVariables.ReadUtf8 = true;
                        continue;
                    }
                    if (arg == "--write-utf8")
                    {
                        GlobalVariables.WriteUtf8 = true;
                        continue;
                    }
                    if (arg == "--no-wrap")
                    {
                        GlobalVariables.NoWrap = true;
                        continue;
                    }
                    if (arg.StartsWith("-j"))
                    {
                        GlobalVariables.jisStrings.Add(arg.Substring(2));
                        continue;
                    }
                    Match match = Regex.Match(arg, @"--(?<name>\w+)=(?<value>.*)$"); //--nome=valor
                    if (!match.Success)
                    {
                        unnamedArgsList.Add(arg);
                        continue;
                    }

                    string name = match.Groups["name"].Value;
                    string value = match.Groups["value"].Value;
                    switch (name)
                    {
                        case "format":
                            options.Format = value;
                            break;
                        case "output":
                            GlobalVariables.OutputFormat = value;
                            break;
                    }
                }

                unnamedArgs = unnamedArgsList.ToArray();
                return options;
            }

            public string? Format
            {
                get;
                private set;
            }
        }
    }
}
