using SpecHygiene.Models;

namespace SpecHygiene.Reporters
{
    public interface IReporter
    {
        string OutputFileName { get; }
        Task GenerateAsync(DuplicateAnalysisReport report, string outputDirectory);
    }
}
