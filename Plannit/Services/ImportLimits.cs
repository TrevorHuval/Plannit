namespace Plannit.Services;

/// <summary>
/// Ceilings on what one import may add. Statement files are capped at 10 MB by the upload limit, but
/// a file that size can still hold hundreds of thousands of rows or a single multi-megabyte field,
/// and every row is kept. On a small shared host that is a way for one signed-in user to fill the
/// disk, so imports stop at a per-file row count and a per-user transaction total, and long
/// descriptions are shortened to the length the manual entry form already enforces.
/// </summary>
public static class ImportLimits
{
    /// <summary>Same limit as <c>TransactionFormViewModel.Description</c>.</summary>
    public const int MaxDescriptionLength = 500;
    /// <summary>Bank transaction ids are short; anything longer is not a real id.</summary>
    public const int MaxBankIdLength = 255;

    public const int DefaultMaxRowsPerFile = 50_000;
    public const int DefaultMaxTransactionsPerUser = 250_000;

    /// <summary>Rows read from one file (<c>Import:MaxRowsPerFile</c>).</summary>
    public static int MaxRowsPerFile(IConfiguration? config) =>
        Math.Max(1, config?.GetValue("Import:MaxRowsPerFile", DefaultMaxRowsPerFile) ?? DefaultMaxRowsPerFile);

    /// <summary>Transactions one user may hold in total (<c>Import:MaxTransactionsPerUser</c>).</summary>
    public static int MaxTransactionsPerUser(IConfiguration? config) =>
        Math.Max(1, config?.GetValue("Import:MaxTransactionsPerUser", DefaultMaxTransactionsPerUser) ?? DefaultMaxTransactionsPerUser);

    public static string RowLimitMessage(int maxRows) =>
        $"This file has more than {maxRows:N0} rows. Only the first {maxRows:N0} were read; split the file and import the rest separately.";

    public static string TransactionLimitMessage(int maxTransactions) =>
        $"Your account has reached its limit of {maxTransactions:N0} transactions, so the remaining rows were not imported.";

    /// <summary>Shortens <paramref name="value"/> to at most <paramref name="maxLength"/> characters without splitting a surrogate pair.</summary>
    public static string Clamp(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var cut = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }
}
