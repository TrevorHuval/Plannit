namespace Plannit.Services.Sync;

/// <summary>Parsed result of a SimpleFIN <c>/accounts</c> response.</summary>
public class SimpleFinAccountSet
{
    public List<string> Errors { get; set; } = new();
    public List<SimpleFinAccount> Accounts { get; set; } = new();
}

public class SimpleFinAccount
{
    public string Id { get; set; } = null!;
    public string? Name { get; set; }
    public string? Currency { get; set; }
    public string? OrgName { get; set; }
    public decimal? Balance { get; set; }
    public DateOnly? BalanceDate { get; set; }
    public List<SimpleFinTransaction> Transactions { get; set; } = new();
}

public class SimpleFinTransaction
{
    public string Id { get; set; } = null!;
    public DateOnly? Posted { get; set; }
    public decimal? Amount { get; set; }
    public string? Description { get; set; }
    public string? Payee { get; set; }
    public string? Memo { get; set; }

    /// <summary>Best available human description: payee falls back to description, then memo.</summary>
    public string BestDescription =>
        FirstNonBlank(Payee, Description, Memo) ?? "(no description)";

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>
/// Thrown when the provider rejects the access URL (HTTP 401/403 or an auth-shaped error),
/// signalling the connection must be re-linked with a fresh setup token.
/// </summary>
public class SimpleFinAuthException : Exception
{
    public SimpleFinAuthException(string message) : base(message) { }
}
