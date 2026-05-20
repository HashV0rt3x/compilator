namespace Compilator.Models;

public class TestCaseResult
{
    public int TestNumber { get; set; }
    public Verdict Verdict { get; set; }
    public long TimeMs { get; set; }
    public long MemoryKb { get; set; }
    public string? ErrorMessage { get; set; }
}
