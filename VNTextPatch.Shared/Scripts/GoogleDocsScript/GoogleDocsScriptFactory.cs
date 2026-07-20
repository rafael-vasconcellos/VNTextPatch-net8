namespace VNTextPatch.Shared.Scripts
{
    public class GoogleDocsScriptFactory
    {
        public static IScriptCollection Build(string spreadsheetId)
        {
            return new GoogleDocsScriptCollection(spreadsheetId);
        }
    }
}