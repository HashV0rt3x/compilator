namespace Compilator.Models;

public class SubmitResponse
{
    public Verdict FinalVerdict { get; set; }
    public int PassedTests { get; set; }
    public int TotalTests { get; set; }
    public List<TestCaseResult> TestResults { get; set; } = [];
    public string? CompilationError { get; set; }
}
