using Compilator.Models;
using Microsoft.Extensions.Options;

namespace Compilator.Services;

public class JudgeService(
    TestCaseService testCaseService,
    SandboxService sandbox,
    IOptions<JudgeOptions> options,
    ILogger<JudgeService> logger)
{
    private readonly JudgeOptions _options = options.Value;

    // Caps concurrent judge sessions across all API requests
    private readonly SemaphoreSlim _slots = new(
        options.Value.MaxParallelContainers,
        options.Value.MaxParallelContainers);

    public async Task<SubmitResponse> JudgeAsync(SubmitRequest request, CancellationToken ct = default)
    {
        List<TestCase> testCases;
        try { testCases = testCaseService.LoadTestCases(request.Problem); }
        catch (DirectoryNotFoundException)
        { return ErrorResponse($"Problem '{request.Problem}' not found"); }

        if (testCases.Count == 0)
            return ErrorResponse("No test cases available");

        await _slots.WaitAsync(ct);
        try
        {
            return await RunJudgeAsync(request, testCases, ct);
        }
        finally { _slots.Release(); }
    }

    private async Task<SubmitResponse> RunJudgeAsync(
        SubmitRequest request, List<TestCase> testCases, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "judge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            // Write source file to disk (needed for native/jvm compilers)
            var sourceFile = GetSourceFileName(request.Language);
            await File.WriteAllTextAsync(Path.Combine(workDir, sourceFile), request.Code, ct);

            logger.LogInformation("Compiling {Lang} for {Problem}", request.Language, request.Problem);

            var compiled = await sandbox.CompileAsync(request.Language, request.Code, workDir, ct);
            if (!compiled.Success)
                return new SubmitResponse
                {
                    FinalVerdict = Verdict.CompilationError,
                    TotalTests = testCases.Count,
                    CompilationError = compiled.Error
                };

            logger.LogInformation("Running {Count} tests for {Problem}", testCases.Count, request.Problem);

            var results = new TestCaseResult[testCases.Count];
            for (var i = 0; i < testCases.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tc = testCases[i];

                var run = await sandbox.RunTestAsync(
                    request.Language, compiled,
                    tc.Input, tc.ExpectedOutput,
                    request.TimeLimitMs, request.MemoryLimitMb,
                    workDir, ct);

                results[i] = new TestCaseResult
                {
                    TestNumber   = tc.Number,
                    Verdict      = MapVerdict(run.Verdict),
                    TimeMs       = run.TimeMs,
                    MemoryKb     = run.MemoryKb,
                    ErrorMessage = run.ErrorMessage
                };
            }

            return BuildResponse(results, testCases.Count);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SubmitResponse BuildResponse(TestCaseResult[] results, int total)
    {
        var ordered = results.OrderBy(r => r.TestNumber).ToList();
        return new SubmitResponse
        {
            FinalVerdict = DetermineVerdict(ordered),
            PassedTests  = ordered.Count(r => r.Verdict == Verdict.Accepted),
            TotalTests   = total,
            TestResults  = ordered
        };
    }

    private static Verdict DetermineVerdict(IReadOnlyList<TestCaseResult> results)
    {
        ReadOnlySpan<Verdict> priority =
        [
            Verdict.InternalError,
            Verdict.CompilationError,
            Verdict.RuntimeError,
            Verdict.MemoryLimitExceeded,
            Verdict.TimeLimitExceeded,
            Verdict.WrongAnswer
        ];
        foreach (var p in priority)
            if (results.Any(r => r.Verdict == p)) return p;
        return Verdict.Accepted;
    }

    private static Verdict MapVerdict(string v) => v switch
    {
        "AC"  => Verdict.Accepted,
        "WA"  => Verdict.WrongAnswer,
        "TLE" => Verdict.TimeLimitExceeded,
        "MLE" => Verdict.MemoryLimitExceeded,
        "RE"  => Verdict.RuntimeError,
        _     => Verdict.InternalError
    };

    private static string GetSourceFileName(Language lang) => lang switch
    {
        Language.Cpp     => "solution.cpp",
        Language.C       => "solution.c",
        Language.Java    => "Main.java",
        Language.Python3 => "solution.py",
        Language.CSharp  => "Solution.cs",
        Language.Go      => "solution.go",
        _ => "solution.txt"
    };

    private static SubmitResponse ErrorResponse(string message) => new()
    {
        FinalVerdict = Verdict.InternalError,
        CompilationError = message
    };

    private void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete temp dir {Path}", path); }
    }
}
