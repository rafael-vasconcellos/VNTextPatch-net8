using Microsoft.Extensions.Logging;

namespace VNTextPatch.Shared
{
    /// <summary>
    /// Optional contract for scripts whose useful data is structured metadata
    /// rather than translatable dialogue lines. The structure is exported to
    /// the text collection and can later be imported to patch the source file.
    /// </summary>
    public interface IStructuredScriptData
    {
        /// <summary>
        /// True when the currently loaded source file supports structured export.
        /// </summary>
        bool SupportsStructuredData { get; }

        /// <summary>
        /// Imports user-edited structured metadata and writes a patched source file.
        /// </summary>
        void ImportStructuredData(ScriptLocation source, ScriptLocation destination);

        /// <summary>
        /// Exports the loaded metadata to a JSON or JSONL file.
        /// The destination extension determines the JSON representation.
        /// </summary>
        void ExportStructuredData(ScriptLocation destination);
        void TryExportStructuredData(ScriptLocation destination, ScriptLocation source)
        {
            string textScriptName = destination.ScriptName;
            // Do not overwrite an existing metadata file: it may contain
            // user edits from an earlier extraction.
            if (!destination.Collection.Exists(textScriptName))
                destination.Collection.Add(textScriptName);

            string structuredPath = destination.ToFilePath();
            bool needsExport = !File.Exists(structuredPath) ||
                                new FileInfo(structuredPath).Length == 0;

            if (needsExport)
            {
                ExportStructuredData(destination);
                AppLogging.Default.LogInformation($"{source.ScriptName} -> {textScriptName}: structured metadata");
            }
            else
            {
                AppLogging.Default.LogInformation($"{source.ScriptName}: structured metadata already exists; keeping it");
            }
        }
    }
}
