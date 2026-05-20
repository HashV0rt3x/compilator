using System.ComponentModel.DataAnnotations;

namespace Compilator.Models;

public class SubmitRequest
{
    [Required]
    public string Problem { get; set; } = string.Empty;

    [Required]
    public Language Language { get; set; }

    [Required]
    [MaxLength(200_000)] // ~200 KB
    public string Code { get; set; } = string.Empty;

    [Range(100, 30000)]
    public int TimeLimitMs { get; set; } = 2000;

    [Range(16, 1024)]
    public int MemoryLimitMb { get; set; } = 256;
}
