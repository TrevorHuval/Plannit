using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Plannit.Models.ViewModels;

namespace Plannit.Services;

public class PositionsCsvImportService
{
    public static bool LooksLikePositionsExport(List<string> headers) =>
        headers.Any(h => h.Trim().Equals("Symbol", StringComparison.OrdinalIgnoreCase)) &&
        headers.Any(h => h.Trim().Equals("Current value", StringComparison.OrdinalIgnoreCase));

    public PositionsImportPreview Parse(Stream csvStream)
    {
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        });

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.ToList() ?? new List<string>();

        var symbolIdx = headers.FindIndex(h => h.Trim().Equals("Symbol", StringComparison.OrdinalIgnoreCase));
        var descIdx = headers.FindIndex(h => h.Trim().Equals("Description", StringComparison.OrdinalIgnoreCase));
        var valueIdx = headers.FindIndex(h => h.Trim().Equals("Current value", StringComparison.OrdinalIgnoreCase));

        var preview = new PositionsImportPreview();

        if (valueIdx < 0)
        {
            preview.Error = "Could not find a 'Current value' column in this file.";
            return preview;
        }

        while (csv.Read())
        {
            var valueStr = csv.GetField(valueIdx);

            if (TryParseCurrency(valueStr, out var value))
            {
                var symbol = symbolIdx >= 0 ? csv.GetField(symbolIdx) ?? "" : "";
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                var description = descIdx >= 0 ? csv.GetField(descIdx) ?? "" : "";
                preview.Positions.Add(new PositionLineViewModel
                {
                    Symbol = symbol.Trim(),
                    Description = description.Trim(),
                    Value = value
                });
                preview.Total += value;
                continue;
            }

            var firstField = csv.GetField(0);
            if (firstField is null) continue;

            var dateMatch = Regex.Match(firstField, @"Date\s+downloaded\s+([A-Za-z]{3}-\d{1,2}-\d{4})", RegexOptions.IgnoreCase);
            if (dateMatch.Success &&
                DateOnly.TryParseExact(dateMatch.Groups[1].Value, ["MMM-d-yyyy", "MMM-dd-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var downloadedDate))
            {
                preview.AsOfDate = downloadedDate;
                preview.DateFromFile = true;
            }
        }

        if (!preview.DateFromFile)
            preview.AsOfDate = DateOnly.FromDateTime(DateTime.Today);

        preview.Success = preview.Positions.Count > 0;
        if (!preview.Success)
            preview.Error = "No positions with a parseable 'Current value' were found in this file.";

        return preview;
    }

    private static bool TryParseCurrency(string? value, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = value.Trim().Replace("$", "").Replace(",", "");
        if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
            cleaned = "-" + cleaned[1..^1];
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
}

public class PositionsImportPreview
{
    public bool Success { get; set; }
    public decimal Total { get; set; }
    public DateOnly AsOfDate { get; set; }
    public bool DateFromFile { get; set; }
    public List<PositionLineViewModel> Positions { get; set; } = new();
    public string? Error { get; set; }
}
