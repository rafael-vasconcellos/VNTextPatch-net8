using System.Collections.Generic;

namespace VNTextPatch.Shared
{
    public interface IScriptCollection
    {
        string Name { get; }

        IEnumerable<string> Scripts { get; }

        IScript GetTemporaryScript();
        bool Exists(string scriptName);
        void Add(string scriptName);
        void Add(string scriptName, ScriptLocation copyFrom);
        IEnumerable<ScriptString> GetStrings(string scriptName)
        {
            IScript script = GetTemporaryScript();
            script.Load(new ScriptLocation(this, scriptName));
            return script.GetStrings();
        }
        List<ScriptString> GetMessageStrings(string scriptName)
        {
            return GetStrings(scriptName).Where(s => s.Type == ScriptStringType.Message).ToList();
        }
    }
}
