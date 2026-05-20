using Compilator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Compilator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController(TestCaseService testCaseService) : ControllerBase
{
    [HttpGet("problems")]
    public ActionResult<IEnumerable<ProblemInfo>> GetProblems()
    {
        var problems = testCaseService.GetProblems()
            .Select(id => new ProblemInfo
            {
                ProblemId = id,
                TestCount = testCaseService.GetTestCount(id)
            });

        return Ok(problems);
    }

    [HttpGet("problems/{problemId}/test-count")]
    public ActionResult<int> GetTestCount(string problemId)
    {
        var count = testCaseService.GetTestCount(problemId);
        if (count == 0)
            return NotFound($"Problem '{problemId}' not found or has no tests");

        return Ok(count);
    }
}

public class ProblemInfo
{
    public string ProblemId { get; set; } = string.Empty;
    public int TestCount { get; set; }
}
