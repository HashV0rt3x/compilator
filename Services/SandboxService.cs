using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Compilator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Compilator.Services;

public class CompileResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    // For C#: in-memory assembly bytes
    public byte[]? AssemblyBytes { get; init; }
    // For native/jvm: path to compiled output
    public string? BinaryPath { get; init; }
}

public class RunResult
{
    public string Verdict { get; init; } = "IE";
    public long TimeMs { get; init; }
    public long MemoryKb { get; init; }
    public string? ErrorMessage { get; init; }
}

public class SandboxService(ILogger<SandboxService> logger)
{
    // Assemblies loaded into every C# submission for convenience
    private static readonly IReadOnlyList<MetadataReference> CSharpReferences = BuildCSharpReferences();

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<CompileResult> CompileAsync(Language language, string code, string workDir,
        CancellationToken ct = default)
    {
        return language switch
        {
            Language.CSharp  => CompileCSharp(code),
            Language.Cpp     => await CompileProcessAsync("g++", $"-O2 -o \"{workDir}/solution\" \"{workDir}/solution.cpp\"", ct),
            Language.C       => await CompileProcessAsync("gcc", $"-O2 -o \"{workDir}/solution\" \"{workDir}/solution.c\" -lm", ct),
            Language.Java    => await CompileProcessAsync("javac", $"\"{workDir}/Main.java\"", ct),
            Language.Python3 => new CompileResult { Success = true },  // interpreted
            Language.Go      => await CompileProcessAsync("go", $"build -o \"{workDir}/solution\" \"{workDir}/solution.go\"", ct),
            _ => new CompileResult { Success = false, Error = "Unknown language" }
        };
    }

    public async Task<RunResult> RunTestAsync(
        Language language,
        CompileResult compiled,
        string input,
        string expectedOutput,
        int timeLimitMs,
        int memoryLimitMb,
        string workDir,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (language == Language.CSharp)
            return RunCSharpInProcess(compiled.AssemblyBytes!, input, expectedOutput, timeLimitMs, sw);

        var (cmd, args) = language switch
        {
            Language.Cpp     => ($"{workDir}/solution", ""),
            Language.C       => ($"{workDir}/solution", ""),
            Language.Go      => ($"{workDir}/solution", ""),
            Language.Java    => ("java", $"-Xmx{memoryLimitMb}m -Xss64m -cp \"{workDir}\" Main"),
            Language.Python3 => ("python3", $"\"{workDir}/solution.py\""),
            _ => throw new InvalidOperationException($"Unhandled language {language}")
        };

        var (exitCode, stdout, stderr, memoryKb) = await RunProcessAsync(cmd, args, input, timeLimitMs, ct);
        sw.Stop();

        if (exitCode == -2) // our timeout sentinel
            return new RunResult { Verdict = "TLE", TimeMs = sw.ElapsedMilliseconds };

        if (exitCode != 0)
            return new RunResult
            {
                Verdict = "RE",
                TimeMs = sw.ElapsedMilliseconds,
                MemoryKb = memoryKb,
                ErrorMessage = stderr.Length > 512 ? stderr[..512] : stderr
            };

        if (sw.ElapsedMilliseconds > timeLimitMs)
            return new RunResult { Verdict = "TLE", TimeMs = sw.ElapsedMilliseconds, MemoryKb = memoryKb };

        var verdict = Normalize(stdout) == Normalize(expectedOutput) ? "AC" : "WA";
        return new RunResult { Verdict = verdict, TimeMs = sw.ElapsedMilliseconds, MemoryKb = memoryKb };
    }

    // ── C# in-process compile (Roslyn) ────────────────────────────────────────

    private static CompileResult CompileCSharp(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);

        var compilation = CSharpCompilation.Create(
            assemblyName: "UserSolution_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: [syntaxTree],
            references: CSharpReferences,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release)
        );

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            return new CompileResult { Success = false, Error = string.Join("\n", errors) };
        }

        return new CompileResult { Success = true, AssemblyBytes = ms.ToArray() };
    }

    // ── C# in-process run ─────────────────────────────────────────────────────

    private RunResult RunCSharpInProcess(
        byte[] assemblyBytes, string input, string expectedOutput,
        int timeLimitMs, Stopwatch sw)
    {
        string? stdout = null;
        string? errorMessage = null;
        long peakMemoryKb = 0;

        // Collectible context — assembly is unloaded after execution
        var loadCtx = new AssemblyLoadContext("submission_" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var thread = new Thread(() =>
            {
                var originalIn  = Console.In;
                var originalOut = Console.Out;
                try
                {
                    using var inputReader  = new StringReader(input);
                    using var outputWriter = new StringWriter();
                    Console.SetIn(inputReader);
                    Console.SetOut(outputWriter);

                    var assembly   = loadCtx.LoadFromStream(new MemoryStream(assemblyBytes));
                    var entryPoint = assembly.EntryPoint
                        ?? throw new InvalidOperationException("No entry point found");

                    var memBefore = GC.GetAllocatedBytesForCurrentThread();
                    entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0
                        ? null
                        : new object?[] { Array.Empty<string>() });
                    var memAfter = GC.GetAllocatedBytesForCurrentThread();

                    stdout = outputWriter.ToString();
                    var allocatedBytes = memAfter - memBefore;
                    logger.LogDebug("CSharp memory: before={Before} after={After} diff={Diff} bytes", memBefore, memAfter, allocatedBytes);
                    // round up so values < 1KB still show as 1
                    Interlocked.Exchange(ref peakMemoryKb, allocatedBytes > 0 ? Math.Max(1, allocatedBytes / 1024) : 0);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    errorMessage = inner.Message;
                }
                finally
                {
                    Console.SetIn(originalIn);
                    Console.SetOut(originalOut);
                }
            });

            thread.Start();
            var finished = thread.Join(timeLimitMs);
            sw.Stop();

            if (!finished)
            {
                try { thread.Interrupt(); } catch { }
                return new RunResult { Verdict = "TLE", TimeMs = sw.ElapsedMilliseconds };
            }
        }
        finally
        {
            loadCtx.Unload();
        }

        if (errorMessage != null)
            return new RunResult
            {
                Verdict = "RE",
                TimeMs = sw.ElapsedMilliseconds,
                MemoryKb = peakMemoryKb,
                ErrorMessage = errorMessage.Length > 512 ? errorMessage[..512] : errorMessage
            };

        if (sw.ElapsedMilliseconds > timeLimitMs)
            return new RunResult { Verdict = "TLE", TimeMs = sw.ElapsedMilliseconds, MemoryKb = peakMemoryKb };

        var verdict = Normalize(stdout ?? "") == Normalize(expectedOutput) ? "AC" : "WA";
        return new RunResult { Verdict = verdict, TimeMs = sw.ElapsedMilliseconds, MemoryKb = peakMemoryKb };
    }

    // ── Subprocess compile ────────────────────────────────────────────────────

    private async Task<CompileResult> CompileProcessAsync(string cmd, string args, CancellationToken ct)
    {
        var (exitCode, _, stderr, _) = await RunProcessAsync(cmd, args, null, 30_000, ct);
        return exitCode == 0
            ? new CompileResult { Success = true }
            : new CompileResult { Success = false, Error = stderr };
    }

    // ── Generic process runner ────────────────────────────────────────────────

    // Returns exitCode = -2 on timeout
    private async Task<(int exitCode, string stdout, string stderr, long memoryKb)> RunProcessAsync(
        string fileName, string arguments, string? stdin, int timeoutMs, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true
            }
        };

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        const int maxOutputBytes = 10 * 1024 * 1024; // 10 MB cap

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && stdoutSb.Length < maxOutputBytes)
                stdoutSb.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && stderrSb.Length < maxOutputBytes)
                stderrSb.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin != null)
            await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        long peakMemoryKb = 0;
        using var memoryPoller = new Timer(_ =>
        {
            try
            {
                process.Refresh();
                var kb = process.WorkingSet64 / 1024;
                if (kb > Interlocked.Read(ref peakMemoryKb))
                    Interlocked.Exchange(ref peakMemoryKb, kb);
            }
            catch { }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogDebug("Process {File} killed after {Ms}ms timeout", fileName, timeoutMs);
            return (-2, "", "Time limit exceeded", 0);
        }
        finally
        {
            await memoryPoller.DisposeAsync();
        }

        return (process.ExitCode, stdoutSb.ToString(), stderrSb.ToString(), Interlocked.Read(ref peakMemoryKb));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    private static IReadOnlyList<MetadataReference> BuildCSharpReferences()
    {
        // Load all assemblies already in the current AppDomain — covers System.*, LINQ, etc.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

        // Ensure the runtime directory assemblies are included
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
        {
            if (refs.All(r => ((PortableExecutableReference)r).FilePath != dll))
                refs.Add(MetadataReference.CreateFromFile(dll));
        }

        return refs;
    }
}
