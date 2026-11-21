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
        /// Exports the loaded metadata to a JSON or JSONL file.
        /// The destination extension determines the JSON representation.
        /// </summary>
        void ExportStructuredData(ScriptLocation destination);

        /// <summary>
        /// Imports user-edited structured metadata and writes a patched source file.
        /// </summary>
        void ImportStructuredData(ScriptLocation source, ScriptLocation destination);
    }
}
