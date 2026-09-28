namespace Plannit.Models.Entities;

/// <summary>
/// Server-side registry entry for a staged statement upload (audit P2-04). The staged file on disk
/// is only ever reachable through this row: it binds the random file id to the uploading user, the
/// target account and the import step it may be used for, and it can be consumed exactly once.
/// Client-posted file ids are never trusted on their own.
/// </summary>
public class TempUpload
{
    /// <summary>The staged file's name on disk (without extension) and the id round-tripped to the client.</summary>
    public Guid Id { get; set; }

    public string UserId { get; set; } = null!;

    /// <summary>The account chosen at upload time; confirm steps must post the same account.</summary>
    public int AccountId { get; set; }

    /// <summary>Which confirm step may use the file: CsvMap, PositionsCsv or PdfStatement.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Whitelisted lowercase extension including the dot (.csv or .pdf).</summary>
    public string Extension { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }

    /// <summary>Size of the staged file, used for the per-user storage quota.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Set atomically when a confirm step claims the upload; a claimed upload can never be reused.</summary>
    public DateTime? ConsumedUtc { get; set; }

    public Microsoft.AspNetCore.Identity.IdentityUser User { get; set; } = null!;
}
