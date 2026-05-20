using Compilator.Models;
using Microsoft.Extensions.Options;

namespace Compilator.Services;

public class JudgeOptions
{
    public string TestCasesBasePath { get; set; } = "/opt/judge/testcases";
    public int MaxParallelContainers { get; set; } = 4;
}

public class TestCase
{
    public int Number { get; set; }
    public string Input { get; set; } = string.Empty;
    public string ExpectedOutput { get; set; } = string.Empty;
}

public class TestCaseService(IOptions<JudgeOptions> options, ILogger<TestCaseService> logger)
{
    private readonly JudgeOptions _options = options.Value;

    public IReadOnlyList<string> GetProblems()
    {
        var basePath = _options.TestCasesBasePath;
        if (!Directory.Exists(basePath))
            return [];

        return Directory.GetDirectories(basePath)
            .Select(Path.GetFileName)
            .Where(name => name != null)
            .Select(name => name!)
            .OrderBy(x => x)
            .ToList();
    }

    public int GetTestCount(string problemId)
    {
        var problemPath = GetProblemPath(problemId);
        if (!Directory.Exists(problemPath))
            return 0;

        return Directory.GetFiles(problemPath, "*.in").Length;
    }

    public List<TestCase> LoadTestCases(string problemId)
    {
        var problemPath = GetProblemPath(problemId);
        if (!Directory.Exists(problemPath))
            throw new DirectoryNotFoundException($"Problem '{problemId}' not found at {problemPath}");

        var inputFiles = Directory.GetFiles(problemPath, "*.in")
            .OrderBy(f => f)
            .ToList();

        if (inputFiles.Count == 0)
            throw new InvalidOperationException($"No test cases found for problem '{problemId}'");

        var testCases = new List<TestCase>(inputFiles.Count);
        foreach (var inputFile in inputFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(inputFile);
            var outputFile = Path.Combine(problemPath, baseName + ".out");

            if (!File.Exists(outputFile))
            {
                logger.LogWarning("Missing output file for {InputFile}", inputFile);
                continue;
            }

            if (!int.TryParse(baseName, out var testNumber))
            {
                logger.LogWarning("Cannot parse test number from {BaseName}", baseName);
                continue;
            }

            testCases.Add(new TestCase
            {
                Number = testNumber,
                Input = File.ReadAllText(inputFile),
                ExpectedOutput = File.ReadAllText(outputFile)
            });
        }

        return testCases;
    }

    private string GetProblemPath(string problemId) =>
        Path.Combine(_options.TestCasesBasePath, problemId);
}
