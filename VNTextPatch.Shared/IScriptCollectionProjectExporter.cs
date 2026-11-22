namespace VNTextPatch.Shared
{
    /// <summary>
    /// Optional capability for collections that can export the editable/project
    /// representation of their source files alongside extracted text.
    /// </summary>
    public interface IScriptCollectionProjectExporter
    {
        /// <summary>
        /// Exports project-side files into a directory. Implementations should
        /// preserve existing destination files so repeated extraction does not
        /// overwrite manual edits.
        /// </summary>
        void ExportProjectFiles(string destinationDirectory);
    }
}
