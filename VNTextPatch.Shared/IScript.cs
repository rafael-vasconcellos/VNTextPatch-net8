using System.Collections.Generic;


namespace VNTextPatch.Shared
{
    public interface IReadOnlyScript
    {
        string? Extension { get; }
        void Load(ScriptLocation location);
        IEnumerable<ScriptString> GetStrings();
    }
    public interface IScript : IReadOnlyScript
    {
        void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location);
    }
}
