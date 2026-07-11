using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;

namespace Plannit.Services;

public class OfxImportService
{
    private readonly ApplicationDbContext _db;

    public OfxImportService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ImportResultViewModel> ImportAsync(Stream ofxStream, int accountId, string fileName)
    {
        var result = new ImportResultViewModel { FileName = fileName };

        using var reader = new StreamReader(ofxStream);
        var content = await reader.ReadToEndAsync();

        var ofxTransactions = ParseTransactions(content);
        result.TotalRows = ofxTransactions.Count;

        var existingHashes = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.ImportHash != null)
            .Select(t => t.ImportHash!)
            .ToHashSetAsync();

        var existingFitIds = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.OfxFitId != null)
            .Select(t => t.OfxFitId!)
            .ToHashSetAsync();

        var batch = new ImportBatch
        {
            AccountId = accountId,
            FileName = fileName,
            ImportedAt = DateTime.UtcNow
        };
        _db.ImportBatches.Add(batch);

        var transactions = new List<Transaction>();
        int rowNumber = 0;

        foreach (var ofxTxn in ofxTransactions)
        {
            rowNumber++;

            try
            {
                if (ofxTxn.Date is null)
                {
                    result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = "Missing or unparseable date (DTPOSTED)" });
                    result.ErrorCount++;
                    continue;
                }

                if (ofxTxn.Amount is null)
                {
                    result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = "Missing or unparseable amount (TRNAMT)" });
                    result.ErrorCount++;
                    continue;
                }

                var description = ofxTxn.Name ?? ofxTxn.Memo ?? "";
                if (string.IsNullOrWhiteSpace(description))
                {
                    result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Message = "No NAME or MEMO found" });
                    result.ErrorCount++;
                    continue;
                }

                if (!string.IsNullOrEmpty(ofxTxn.FitId) && existingFitIds.Contains(ofxTxn.FitId))
                {
                    result.DuplicateCount++;
                    continue;
                }

                var hash = CsvImportService.ComputeImportHash(accountId, ofxTxn.Date.Value, ofxTxn.Amount.Value, description);

                if (existingHashes.Contains(hash))
                {
                    result.DuplicateCount++;
                    continue;
                }

                existingHashes.Add(hash);
                if (!string.IsNullOrEmpty(ofxTxn.FitId))
                    existingFitIds.Add(ofxTxn.FitId);

                transactions.Add(new Transaction
                {
                    AccountId = accountId,
                    Date = ofxTxn.Date.Value,
                    Amount = ofxTxn.Amount.Value,
                    Description = description.Trim(),
                    OriginalDescription = description,
                    ImportHash = hash,
                    OfxFitId = ofxTxn.FitId
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

    internal static List<OfxTransaction> ParseTransactions(string content)
    {
        var transactions = new List<OfxTransaction>();

        var stmtTrnPattern = new Regex(
            @"<STMTTRN>(.*?)</STMTTRN>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in stmtTrnPattern.Matches(content))
        {
            var block = match.Groups[1].Value;
            transactions.Add(ParseTransaction(block));
        }

        if (transactions.Count == 0)
        {
            var openPattern = new Regex(@"<STMTTRN>", RegexOptions.IgnoreCase);
            var openMatches = openPattern.Matches(content);

            for (int i = 0; i < openMatches.Count; i++)
            {
                int start = openMatches[i].Index + openMatches[i].Length;
                int end = (i + 1 < openMatches.Count) ? openMatches[i + 1].Index : content.Length;
                var block = content[start..end];
                transactions.Add(ParseTransaction(block));
            }
        }

        return transactions;
    }

    private static OfxTransaction ParseTransaction(string block)
    {
        return new OfxTransaction
        {
            Date = ParseOfxDate(GetTagValue(block, "DTPOSTED")),
            Amount = ParseOfxAmount(GetTagValue(block, "TRNAMT")),
            FitId = GetTagValue(block, "FITID"),
            Name = GetTagValue(block, "NAME"),
            Memo = GetTagValue(block, "MEMO"),
            TrnType = GetTagValue(block, "TRNTYPE")
        };
    }

    internal static string? GetTagValue(string block, string tagName)
    {
        var pattern = new Regex(
            $@"<{tagName}>\s*(.+?)(?:\s*<|\s*$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var match = pattern.Match(block);
        if (!match.Success) return null;

        var value = match.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static DateOnly? ParseOfxDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var dateStr = value.Trim();
        // Strip timezone offset like [0:GMT] or [-5:EST]
        var bracketIdx = dateStr.IndexOf('[');
        if (bracketIdx >= 0)
            dateStr = dateStr[..bracketIdx].Trim();

        string[] formats = ["yyyyMMddHHmmss.fff", "yyyyMMddHHmmss", "yyyyMMdd"];
        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(dateStr, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
        }

        return null;
    }

    internal static decimal? ParseOfxAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().Replace(",", "");
        if (decimal.TryParse(cleaned, CultureInfo.InvariantCulture, out var amount))
            return amount;
        return null;
    }

    internal class OfxTransaction
    {
        public DateOnly? Date { get; set; }
        public decimal? Amount { get; set; }
        public string? FitId { get; set; }
        public string? Name { get; set; }
        public string? Memo { get; set; }
        public string? TrnType { get; set; }
    }
}
