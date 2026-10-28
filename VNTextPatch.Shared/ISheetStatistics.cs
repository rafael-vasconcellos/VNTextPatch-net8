namespace VNTextPatch.Shared
{
    public interface ISheetStatistics
    {
        int Translated { get; }
        int Total { get; set; }
        int Checked { get; }
        int Edited { get; }

        void Reset();
    }
}
