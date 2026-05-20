using Compilator.Models;
using Compilator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Compilator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JudgeController(JudgeService judgeService, ILogger<JudgeController> logger) : ControllerBase
{
    /// <summary>
    /// Submit code as JSON body or as a file (multipart/form-data).
    /// JSON: { problem, language, code, timeLimitMs?, memoryLimitMb? }
    /// Form: problem, language, timeLimitMs?, memoryLimitMb?, and either 'code' field or 'file' upload.
    /// </summary>
    [HttpPost("submit")]
    public async Task<ActionResult<SubmitResponse>> Submit(CancellationToken cancellationToken)
    {
        SubmitRequest request;

        var contentType = HttpContext.Request.ContentType ?? "";

        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var form = await HttpContext.Request.ReadFormAsync(cancellationToken);

            if (!Enum.TryParse<Language>(form["language"], ignoreCase: true, out var language))
                return BadRequest("Invalid or missing 'language'");

            string? code = null;

            if (form.Files.Count > 0)
            {
                var file = form.Files[0];
                if (file.Length == 0)   return BadRequest("File is empty");
                if (file.Length > 200_000) return BadRequest("File size must not exceed 200 KB");
                using var reader = new StreamReader(file.OpenReadStream());
                code = await reader.ReadToEndAsync(cancellationToken);
            }
            else if (!string.IsNullOrEmpty(form["code"]))
            {
                code = form["code"].ToString();
            }

            if (string.IsNullOrEmpty(code))
                return BadRequest("Provide either 'code' field or a file");

            request = new SubmitRequest
            {
                Problem       = form["problem"].ToString(),
                Language      = language,
                Code          = code,
                TimeLimitMs   = int.TryParse(form["timeLimitMs"],   out var tl) ? tl : 2000,
                MemoryLimitMb = int.TryParse(form["memoryLimitMb"], out var ml) ? ml : 256,
            };
        }
        else
        {
            var json = await HttpContext.Request.ReadFromJsonAsync<SubmitRequest>(cancellationToken);
            if (json is null) return BadRequest("Invalid JSON body");
            request = json;
        }

        if (!TryValidate(request, out var errors))
            return BadRequest(errors);

        logger.LogInformation("Submission: problem={Problem} lang={Lang}", request.Problem, request.Language);

        var response = await judgeService.JudgeAsync(request, cancellationToken);
        return Ok(response);
    }

    private bool TryValidate(SubmitRequest r, out List<string> errors)
    {
        errors = [];
        if (string.IsNullOrEmpty(r.Problem))   errors.Add("'problem' is required");
        if (string.IsNullOrEmpty(r.Code))      errors.Add("'code' is required");
        if (r.Code.Length > 200_000)           errors.Add("Code exceeds 200 KB limit");
        if (r.TimeLimitMs is < 100 or > 30000) errors.Add("'timeLimitMs' must be 100–30000");
        if (r.MemoryLimitMb is < 16 or > 1024) errors.Add("'memoryLimitMb' must be 16–1024");
        return errors.Count == 0;
    }
}
