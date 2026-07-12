using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;

namespace Plannit.Services;

public class CsvImportService
{
    private readonly ApplicationDbContext _db;

    public CsvImportService(ApplicationDbContext db)
    {
        _db = db;
    }

    public (List<string> Headers, List<List<string>> PreviewRows) ReadPreview(Stream csvStream, int maxRows = 5)
    {
        using var reader = new StreamReader(csvStream, leaveOpen: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        });

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.ToList() ?? new List<string>();
        var rows = new List<List<string>>();

        while (csv.Read() && rows.Count < maxRows)
        {
            var row = new List<string>();
            for (int i = 0; i < headers.Count; i++)
                row.Add(csv.GetField(i) ?? "");
            rows.Add(row);
        }

        return (headers, rows);
    }

    public async Task<ImportProfile?> GetProfileAsync(int accountId)
    {
        return await _db.ImportProfiles.FirstOrDefaultAsync(p => p.AccountId == accountId);
    }

    public async Task SaveProfileAsync(int accountId, string dateColumn, string dateFormat,
        string? amountColumn, string? debitColumn, string? creditColumn, string descriptionColumn,
        bool invertAmounts)
    {
        var profile = await _db.ImportProfiles.FirstOrDefaultAsync(p => p.AccountId == accountId);
        if (profile is null)
        {
            profile = new ImportProfile { AccountId = accountId };
            _db.ImportProfiles.Add(profile);
        }

        profile.DateColumn = dateColumn;
        profile.DateFormat = dateFormat;
        profile.AmountColumn = amountColumn;
        profile.DebitColumn = debitColumn;
        profile.CreditColumn = creditColumn;
        profile.DescriptionColumn = descriptionColumn;
        profile.InvertAmounts = invertAmounts;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Heuristically guesses whether an unmapped CSV's amount column shows charges as positive
    /// (inverted relative to this app's convention) by scanning preview rows for the column that
    /// parses as decimal most often, then checking whether most of its values are positive.
    /// </summary>
    public bool SuggestInvertAmounts(bool accountIsLiability, List<string> headers, List<List<string>> previewRows)
    {
        if (!accountIsLiability || previewRows.Count == 0) return false;

        var bestColumn = -1;
        var bestParsed = 0;
        for (var col = 0; col < headers.Count; col++)
        {
            var parsed = previewRows.Count(row => col < row.Count && TryParseAmount(row[col], out _));
            if (parsed > bestParsed)
            {
                bestParsed = parsed;
                bestColumn = col;
            }
        }

        if (bestColumn < 0 || bestParsed == 0) return false;

        var total = 0;
        var positive = 0;
        foreach (var row in previewRows)
        {
            if (bestColumn >= row.Count || !TryParseAmount(row[bestColumn], out var amount)) continue;
            total++;
            if (amount > 0) positive++;
        }

        return total > 0 && positive > total / 2.0;
    }

    public async Task<ImportResultViewModel> ImportAsync(Stream csvStream, int accountId, string fileName,
        string dateColumn, string dateFormat, string? amountColumn, string? debitColumn, string? creditColumn,
        string descriptionColumn, bool invertAmounts = false)
    {
        var result = new ImportResultViewModel { FileName = fileName };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        });

        csv.Read();
        csv.ReadHeader();

        var existingHashes = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.ImportHash != null)
            .Select(t => t.ImportHash!)
            .ToHashSetAsync();

        var batch = new ImportBatch
        {
            AccountId = accountId,
            FileName = fileName,
            ImportedAt = DateTime.UtcNow
        };
        _db.ImportBatches.Add(batch);

        var transactions = new List<Transaction>();
        int rowNumber = 1;
        bool useSingleAmount = !string.IsNullOrEmpty(amountColumn);

        while (csv.Read())
        {
            rowNumber++;
            result.TotalRows++;

            try
            {
                var dateStr = csv.GetField(dateColumn);
                if (string.IsNullOrWhiteSpace(dateStr))
                {
                    result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = "Date is empty" });
                    result.ErrorCount++;
                    continue;
                }

                if (!DateOnly.TryParseExact(dateStr.Trim(), dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = $"Cannot parse date '{dateStr}' with format '{dateFormat}'" });
                    result.ErrorCount++;
                    continue;
                }

                decimal amount;
                if (useSingleAmount)
                {
                    var amountStr = csv.GetField(amountColumn!);
                    if (!TryParseAmount(amountStr, out amount))
                    {
                        result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = $"Cannot parse amount '{amountStr}'" });
                        result.ErrorCount++;
                        continue;
                    }
                }
                else
                {
                    var debitStr = csv.GetField(debitColumn!);
                    var creditStr = csv.GetField(creditColumn!);
                    TryParseAmount(debitStr, out var debit);
                    TryParseAmount(creditStr, out var credit);

                    if (debit == 0 && credit == 0)
                    {
                        result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = "Both debit and credit are zero or empty" });
                        result.ErrorCount++;
                        continue;
                    }

                    amount = credit - debit;
                }

                if (invertAmounts) amount = -amount;

                var originalDesc = csv.GetField(descriptionColumn) ?? "";
                var description = originalDesc.Trim();

                if (string.IsNullOrWhiteSpace(description))
                {
                    result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = "Description is empty" });
                    result.ErrorCount++;
                    continue;
                }

                var hash = ComputeImportHash(accountId, date, amount, originalDesc);

                if (existingHashes.Contains(hash))
                {
                    result.DuplicateCount++;
                    continue;
                }

                existingHashes.Add(hash);

                transactions.Add(new Transaction
                {
                    AccountId = accountId,
                    Date = date,
                    Amount = amount,
                    Description = description,
                    OriginalDescription = originalDesc,
                    ImportBatchId = batch.Id,
                    ImportHash = hash
                });
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = ex.Message });
                result.ErrorCount++;
            }
        }

        batch.RowCount = transactions.Count;

        if (transactions.Count > 0)
        {
            await _db.SaveChangesAsync();

            foreach (var t in transactions)
                t.ImportBatchId = batch.Id;

            _db.Transactions.AddRange(transactions);
            await _db.SaveChangesAsync();
        }

        result.ImportedCount = transactions.Count;
        return result;
    }

    public static string ComputeImportHash(int accountId, DateOnly date, decimal amount, string originalDescription)
    {
        var input = $"{accountId}|{date:yyyy-MM-dd}|{amount}|{originalDescription}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool TryParseAmount(string? value, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = value.Trim().Replace("$", "").Replace(",", "");
        if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
            cleaned = "-" + cleaned[1..^1];
        return decimal.TryParse(cleaned, CultureInfo.InvariantCulture, out amount);
    }
}
