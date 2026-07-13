using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services;
using Plannit.Services.Ai;

namespace Plannit.Controllers;

[Authorize]
public class TransactionsController : Controller
{
    private readonly TransactionService _transactionService;
    private readonly AccountService _accountService;
    private readonly CsvImportService _csvImportService;
    private readonly OfxImportService _ofxImportService;
    private readonly PositionsCsvImportService _positionsCsvImportService;
    private readonly PdfStatementService _pdfStatementService;
    private readonly SnapshotImportService _snapshotImportService;
    private readonly CategorizationService _categorizationService;
    private readonly DataManagementService _dataService;
    private readonly AiSettingsService _aiSettings;
    private readonly SmartCategorizationService _smartCategorization;
    private readonly string _tempUploadPath;

    public TransactionsController(
        TransactionService transactionService,
        AccountService accountService,
        CsvImportService csvImportService,
        OfxImportService ofxImportService,
        PositionsCsvImportService positionsCsvImportService,
        PdfStatementService pdfStatementService,
        SnapshotImportService snapshotImportService,
        CategorizationService categorizationService,
        DataManagementService dataService,
        AiSettingsService aiSettings,
        SmartCategorizationService smartCategorization,
        IWebHostEnvironment env)
    {
        _transactionService = transactionService;
        _accountService = accountService;
        _csvImportService = csvImportService;
        _ofxImportService = ofxImportService;
        _positionsCsvImportService = positionsCsvImportService;
        _pdfStatementService = pdfStatementService;
        _snapshotImportService = snapshotImportService;
        _categorizationService = categorizationService;
        _dataService = dataService;
        _aiSettings = aiSettings;
        _smartCategorization = smartCategorization;
        _tempUploadPath = Path.Combine(env.ContentRootPath, "TempUploads");
    }

    // Search text is echoed back into the page (filter box, export/pagination links);
    // constrain it at the boundary so reflected markup is impossible regardless of encoding.
    private static string? SanitizeSearchText(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return null;
        var cleaned = Regex.Replace(searchText.Trim(), @"[<>""'\\]", "");
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    private static readonly int[] AllowedPageSizes = [25, 50, 100];

    public async Task<IActionResult> Index(int? accountId, DateOnly? startDate, DateOnly? endDate, string? searchText, int? categoryId, int page = 1, int pageSize = 50)
    {
        if (!AllowedPageSizes.Contains(pageSize)) pageSize = 50;
        if (page < 1) page = 1;

        searchText = SanitizeSearchText(searchText);
        var (items, totalCount) = await _transactionService.GetFilteredAsync(accountId, startDate, endDate, searchText, categoryId, page, pageSize);
        var accounts = await _accountService.GetAllAsync();
        var categories = await _categorizationService.GetAllCategoriesAsync();

        var vm = new TransactionListViewModel
        {
            AccountId = accountId,
            StartDate = startDate,
            EndDate = endDate,
            SearchText = searchText,
            CategoryId = categoryId,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)),
            AiConfigured = await _aiSettings.IsConfiguredAsync(),
            Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList(),
            Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList(),
            Transactions = items.Select(t => new TransactionRowViewModel
            {
                Id = t.Id,
                Date = t.Date,
                Amount = t.Amount,
                Description = t.Description,
                AccountName = t.Account.Name,
                AccountType = t.Account.Type,
                CategoryId = t.CategoryId,
                CategoryName = t.Category?.Name,
                Notes = t.Notes,
                SplitGroupId = t.SplitGroupId
            }).ToList()
        };

        return View(vm);
    }

    public async Task<IActionResult> Create()
    {
        var accounts = await _accountService.GetAllAsync();
        return View(new TransactionFormViewModel
        {
            Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TransactionFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var accounts = await _accountService.GetAllAsync();
            model.Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
            return View(model);
        }

        await _transactionService.CreateAsync(model.AccountId, model.Date, model.Amount, model.Description);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id, string? returnUrl)
    {
        var transaction = await _transactionService.GetByIdAsync(id);
        if (transaction is null) return NotFound();

        var accounts = await _accountService.GetAllAsync();
        return View(new TransactionFormViewModel
        {
            Id = transaction.Id,
            AccountId = transaction.AccountId,
            Date = transaction.Date,
            Amount = transaction.Amount,
            Description = transaction.Description,
            Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList(),
            ReturnUrl = returnUrl
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, TransactionFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var accounts = await _accountService.GetAllAsync();
            model.Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
            return View(model);
        }

        var success = await _transactionService.UpdateAsync(id, model.AccountId, model.Date, model.Amount, model.Description);
        if (!success) return NotFound();

        return RedirectToReturnUrlOrIndex(model.ReturnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? returnUrl)
    {
        await _transactionService.DeleteAsync(id);
        return RedirectToReturnUrlOrIndex(returnUrl);
    }

    public async Task<IActionResult> Import()
    {
        var accounts = await _accountService.GetAllAsync();
        return View(new ImportUploadViewModel
        {
            Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Import(ImportUploadViewModel model)
    {
        if (model.Files.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Files), "Please select at least one file to import.");
        }

        if (!ModelState.IsValid)
        {
            var accounts = await _accountService.GetAllAsync();
            model.Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
            return View(model);
        }

        var account = await _accountService.GetByIdAsync(model.AccountId);
        if (account is null) return NotFound();

        foreach (var file in model.Files)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not ".csv" and not ".ofx" and not ".qfx" and not ".pdf")
            {
                ModelState.AddModelError(nameof(model.Files), $"Unsupported file format: {file.FileName}. Supported: CSV, OFX, QFX, PDF.");
                var accounts = await _accountService.GetAllAsync();
                model.Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
                return View(model);
            }
        }

        var multiResult = new MultiImportResultViewModel { AccountName = account.Name };
        var pendingQueue = new List<PendingImportItemViewModel>();
        Directory.CreateDirectory(_tempUploadPath);

        foreach (var file in model.Files)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (ext is ".ofx" or ".qfx")
            {
                using var stream = file.OpenReadStream();
                var result = await _ofxImportService.ImportAsync(stream, model.AccountId, file.FileName);
                result.AccountName = account.Name;
                multiResult.FileResults.Add(result);
            }
            else if (ext == ".pdf")
            {
                var tempId = await SaveTempFileAsync(file, ".pdf");
                pendingQueue.Add(new PendingImportItemViewModel
                {
                    Kind = "PdfStatement",
                    FileName = file.FileName,
                    TempFileId = tempId,
                    TempFileExtension = ".pdf",
                    AccountId = model.AccountId,
                    AccountName = account.Name
                });
            }
            else
            {
                List<string> headers;
                using (var peekStream = file.OpenReadStream())
                {
                    (headers, _) = _csvImportService.ReadPreview(peekStream);
                }

                if (model.PositionsStatement || PositionsCsvImportService.LooksLikePositionsExport(headers))
                {
                    var tempId = await SaveTempFileAsync(file, ".csv");
                    pendingQueue.Add(new PendingImportItemViewModel
                    {
                        Kind = "PositionsCsv",
                        FileName = file.FileName,
                        TempFileId = tempId,
                        TempFileExtension = ".csv",
                        AccountId = model.AccountId,
                        AccountName = account.Name
                    });
                    continue;
                }

                var profile = await _csvImportService.GetProfileAsync(model.AccountId);
                if (profile is not null)
                {
                    using var stream = file.OpenReadStream();
                    var result = await _csvImportService.ImportAsync(stream, model.AccountId, file.FileName,
                        profile.DateColumn, profile.DateFormat,
                        profile.AmountColumn, profile.DebitColumn, profile.CreditColumn,
                        profile.DescriptionColumn, profile.InvertAmounts);
                    result.AccountName = account.Name;
                    multiResult.FileResults.Add(result);
                }
                else
                {
                    var tempId = await SaveTempFileAsync(file, ".csv");
                    pendingQueue.Add(new PendingImportItemViewModel
                    {
                        Kind = "CsvMap",
                        FileName = file.FileName,
                        TempFileId = tempId,
                        TempFileExtension = ".csv",
                        AccountId = model.AccountId,
                        AccountName = account.Name
                    });
                }
            }
        }

        if (pendingQueue.Count > 0)
        {
            if (multiResult.FileResults.Count > 0)
            {
                var categorized = await _categorizationService.ApplyRulesToUncategorizedAsync();
                multiResult.CategorizedCount = categorized;
            }

            TempData["MultiImportResults"] = JsonSerializer.Serialize(multiResult.FileResults);
            TempData["MultiImportAccountName"] = account.Name;

            var first = pendingQueue[0];
            if (pendingQueue.Count > 1)
                TempData["PendingImportQueue"] = JsonSerializer.Serialize(pendingQueue.Skip(1).ToList());

            return await ShowPendingItemAsync(first);
        }

        return await FinalizeMultiImportAsync(multiResult.FileResults, account.Name, model.AccountId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ConfirmImport(ImportMapViewModel model)
    {
        if (!Guid.TryParse(model.TempFileId, out _))
        {
            TempData["Error"] = "Invalid file reference. Please re-upload the file.";
            return RedirectToAction(nameof(Import));
        }

        if (string.IsNullOrEmpty(model.AmountColumn) &&
            (string.IsNullOrEmpty(model.DebitColumn) || string.IsNullOrEmpty(model.CreditColumn)))
        {
            ModelState.AddModelError("", "Provide either an Amount column or both Debit and Credit columns.");
        }

        if (!ModelState.IsValid)
        {
            var tempPath = GetSafeTempPath(model.TempFileId, ".csv");
            if (tempPath is not null && System.IO.File.Exists(tempPath))
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var (headers, rows) = _csvImportService.ReadPreview(stream);
                model.AvailableColumns = headers;
                model.PreviewRows = rows;
            }
            return View("MapColumns", model);
        }

        var tempFilePath = GetSafeTempPath(model.TempFileId, ".csv");
        if (tempFilePath is null || !System.IO.File.Exists(tempFilePath))
        {
            TempData["Error"] = "Upload expired. Please re-upload the file.";
            return RedirectToAction(nameof(Import));
        }

        await _csvImportService.SaveProfileAsync(model.AccountId, model.DateColumn, model.DateFormat,
            model.AmountColumn, model.DebitColumn, model.CreditColumn, model.DescriptionColumn,
            model.InvertAmounts);

        ImportResultViewModel result;
        using (var stream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read))
        {
            result = await _csvImportService.ImportAsync(stream, model.AccountId, model.FileName,
                model.DateColumn, model.DateFormat, model.AmountColumn, model.DebitColumn,
                model.CreditColumn, model.DescriptionColumn, model.InvertAmounts);
        }

        try { System.IO.File.Delete(tempFilePath); } catch { }

        var account = await _accountService.GetByIdAsync(model.AccountId);
        result.AccountName = account?.Name ?? "Unknown";

        return await ContinueImportChainAsync(result, model.AccountId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmSnapshotImport(SnapshotConfirmViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("ConfirmSnapshot", model);
        }

        var snapshot = await _snapshotImportService.UpsertSnapshotAsync(model.AccountId, model.AsOfDate, model.Balance);

        var confirmTempPath = GetSafeTempPath(model.TempFileId, model.TempFileExtension);
        if (confirmTempPath is not null)
        {
            try { System.IO.File.Delete(confirmTempPath); } catch { }
        }

        var result = new ImportResultViewModel
        {
            AccountName = model.AccountName,
            FileName = model.FileName,
            SnapshotOnly = true,
            SnapshotUpdated = snapshot is not null,
            SnapshotBalance = snapshot?.Balance,
            SnapshotDate = snapshot?.Date
        };

        return await ContinueImportChainAsync(result, model.AccountId);
    }

    private static readonly string[] AllowedTempExtensions = [".csv", ".ofx", ".qfx", ".pdf"];

    // Rebuilds the temp path from a parsed Guid and a whitelisted extension so
    // client-posted identifiers can never traverse outside TempUploads.
    private string? GetSafeTempPath(string? tempFileId, string? extension)
    {
        if (!Guid.TryParse(tempFileId, out var id)) return null;
        var ext = string.IsNullOrEmpty(extension) ? ".csv" : extension.ToLowerInvariant();
        if (!AllowedTempExtensions.Contains(ext)) return null;
        return Path.Combine(_tempUploadPath, id.ToString("D") + ext);
    }

    private async Task<string> SaveTempFileAsync(IFormFile file, string extension)
    {
        var tempId = Guid.NewGuid().ToString();
        var tempPath = Path.Combine(_tempUploadPath, tempId + extension);
        using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        return tempId;
    }

    private async Task<IActionResult> ShowPendingItemAsync(PendingImportItemViewModel item)
    {
        var tempPath = GetSafeTempPath(item.TempFileId, item.TempFileExtension);
        if (tempPath is null || !System.IO.File.Exists(tempPath))
        {
            TempData["Error"] = $"Upload expired for {item.FileName}. Please re-upload.";
            return RedirectToAction(nameof(Import));
        }

        switch (item.Kind)
        {
            case "PositionsCsv":
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var preview = _positionsCsvImportService.Parse(stream);
                return View("ConfirmSnapshot", new SnapshotConfirmViewModel
                {
                    AccountId = item.AccountId,
                    AccountName = item.AccountName,
                    FileName = item.FileName,
                    TempFileId = item.TempFileId,
                    TempFileExtension = item.TempFileExtension,
                    SourceType = "PositionsCsv",
                    AsOfDate = preview.AsOfDate,
                    Balance = preview.Total,
                    BalanceFound = preview.Success,
                    DateFound = preview.DateFromFile,
                    Positions = preview.Positions
                });
            }
            case "PdfStatement":
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var preview = _pdfStatementService.Parse(stream);
                return View("ConfirmSnapshot", new SnapshotConfirmViewModel
                {
                    AccountId = item.AccountId,
                    AccountName = item.AccountName,
                    FileName = item.FileName,
                    TempFileId = item.TempFileId,
                    TempFileExtension = item.TempFileExtension,
                    SourceType = "PdfStatement",
                    AsOfDate = preview.AsOfDate ?? DateOnly.FromDateTime(DateTime.Today),
                    Balance = preview.Balance ?? 0,
                    BalanceFound = preview.Balance.HasValue,
                    DateFound = preview.AsOfDate.HasValue,
                    ExtractedText = preview.ExtractedText
                });
            }
            default: // "CsvMap"
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var (headers, previewRows) = _csvImportService.ReadPreview(stream);
                var profile = await _csvImportService.GetProfileAsync(item.AccountId);
                var account = await _accountService.GetByIdAsync(item.AccountId);

                var mapVm = new ImportMapViewModel
                {
                    AccountId = item.AccountId,
                    AccountName = item.AccountName,
                    FileName = item.FileName,
                    TempFileId = item.TempFileId,
                    AvailableColumns = headers,
                    PreviewRows = previewRows
                };

                if (profile is not null)
                {
                    mapVm.DateColumn = profile.DateColumn;
                    mapVm.DateFormat = profile.DateFormat;
                    mapVm.AmountColumn = profile.AmountColumn;
                    mapVm.DebitColumn = profile.DebitColumn;
                    mapVm.CreditColumn = profile.CreditColumn;
                    mapVm.DescriptionColumn = profile.DescriptionColumn;
                    mapVm.InvertAmounts = profile.InvertAmounts;
                }
                else
                {
                    mapVm.InvertAmounts = _csvImportService.SuggestInvertAmounts(
                        account is not null && NetWorthService.IsLiability(account.Type), headers, previewRows);
                }

                return View("MapColumns", mapVm);
            }
        }
    }

    private async Task<IActionResult> ContinueImportChainAsync(ImportResultViewModel newResult, int accountId)
    {
        var priorResultsJson = TempData["MultiImportResults"] as string;
        var queueJson = TempData["PendingImportQueue"] as string;
        var multiAccountName = TempData["MultiImportAccountName"] as string;

        var allResults = new List<ImportResultViewModel>();
        if (priorResultsJson != null)
        {
            var prior = JsonSerializer.Deserialize<List<ImportResultViewModel>>(priorResultsJson);
            if (prior != null) allResults.AddRange(prior);
        }
        allResults.Add(newResult);

        if (!string.IsNullOrEmpty(queueJson))
        {
            var remaining = JsonSerializer.Deserialize<List<PendingImportItemViewModel>>(queueJson);
            if (remaining != null && remaining.Count > 0)
            {
                TempData["MultiImportResults"] = JsonSerializer.Serialize(allResults);
                TempData["MultiImportAccountName"] = multiAccountName;

                var next = remaining[0];
                if (remaining.Count > 1)
                    TempData["PendingImportQueue"] = JsonSerializer.Serialize(remaining.Skip(1).ToList());

                return await ShowPendingItemAsync(next);
            }
        }

        return await FinalizeMultiImportAsync(allResults, multiAccountName ?? newResult.AccountName, accountId);
    }

    private async Task<IActionResult> FinalizeMultiImportAsync(List<ImportResultViewModel> results, string accountName, int accountId)
    {
        var categorized = 0;
        if (results.Any(r => r.ImportedCount > 0))
        {
            categorized = await _categorizationService.ApplyRulesToUncategorizedAsync();
        }

        var multiResult = new MultiImportResultViewModel
        {
            AccountName = accountName,
            FileResults = results,
            CategorizedCount = categorized,
            AccountId = accountId
        };

        if (results.Any(r => r.ImportedCount > 0))
        {
            var account = await _accountService.GetByIdAsync(accountId);
            var latestSnapshotDate = account?.Snapshots.MaxBy(s => s.Date)?.Date;
            var newestTxnDate = await _transactionService.GetLatestTransactionDateAsync(accountId);

            if (newestTxnDate.HasValue && (latestSnapshotDate is null || latestSnapshotDate < newestTxnDate))
            {
                multiResult.ShowSnapshotNudge = true;
                multiResult.NudgeAccountId = accountId;
                multiResult.NudgeLatestSnapshotDate = latestSnapshotDate;
                multiResult.NudgeNewestTransactionDate = newestTxnDate.Value;
            }

            if (await _aiSettings.IsConfiguredAsync())
            {
                multiResult.AiConfigured = true;
                multiResult.UncategorizedRemaining = await _smartCategorization.CountUncategorizedAsync(accountId);
            }
        }

        return View("MultiImportResult", multiResult);
    }

    public async Task<IActionResult> ExportCsv(int? accountId, DateOnly? startDate, DateOnly? endDate, string? searchText, int? categoryId)
    {
        searchText = SanitizeSearchText(searchText);
        var (items, _) = await _transactionService.GetFilteredAsync(accountId, startDate, endDate, searchText, categoryId, 1, int.MaxValue);

        var sb = new StringBuilder();
        sb.AppendLine("Date,Description,Amount,Account,Category,Notes");
        foreach (var t in items)
        {
            var desc = CsvEscape(t.Description);
            var acct = CsvEscape(t.Account.Name);
            var cat = CsvEscape(t.Category?.Name ?? "");
            var notes = CsvEscape(t.Notes ?? "");
            sb.AppendLine($"{t.Date:yyyy-MM-dd},{desc},{t.Amount},{acct},{cat},{notes}");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"transactions-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkSetCategory(List<int> transactionIds, int categoryId, string? returnUrl)
    {
        if (transactionIds.Count == 0)
        {
            TempData["Error"] = "No transactions selected.";
            return RedirectToReturnUrlOrIndex(returnUrl);
        }

        var count = await _dataService.BulkSetCategoryAsync(transactionIds, categoryId);
        TempData["Message"] = $"Updated category for {count} transaction{(count != 1 ? "s" : "")}.";
        return RedirectToReturnUrlOrIndex(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDelete(List<int> transactionIds, string? returnUrl)
    {
        if (transactionIds.Count == 0)
        {
            TempData["Error"] = "No transactions selected.";
            return RedirectToReturnUrlOrIndex(returnUrl);
        }

        var count = await _dataService.BulkDeleteAsync(transactionIds);
        TempData["Message"] = $"Deleted {count} transaction{(count != 1 ? "s" : "")}.";
        return RedirectToReturnUrlOrIndex(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InvertAccountSigns(int accountId, string? returnUrl)
    {
        var count = await _dataService.InvertAccountTransactionSignsAsync(accountId);
        TempData["Message"] = $"Inverted the sign on {count} transaction{(count != 1 ? "s" : "")}.";
        return RedirectToReturnUrlOrIndex(returnUrl, new { accountId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateNotes(int id, string? notes, string? returnUrl)
    {
        await _dataService.UpdateTransactionNotesAsync(id, notes);
        return RedirectToReturnUrlOrIndex(returnUrl);
    }

    public async Task<IActionResult> Split(int id, string? returnUrl)
    {
        var txn = await _transactionService.GetByIdAsync(id);
        if (txn is null) return NotFound();

        var categories = await _categorizationService.GetAllCategoriesAsync();
        var vm = new SplitTransactionViewModel
        {
            TransactionId = txn.Id,
            OriginalDescription = txn.Description,
            OriginalAmount = txn.Amount,
            Date = txn.Date,
            Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList(),
            Splits = new List<SplitLineViewModel>
            {
                new() { Amount = txn.Amount, Description = txn.Description, CategoryId = txn.CategoryId },
                new() { Amount = 0, Description = txn.Description }
            },
            ReturnUrl = returnUrl
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Split(SplitTransactionViewModel model)
    {
        model.Splits = model.Splits.Where(s => s.Amount != 0 || !string.IsNullOrWhiteSpace(s.Description)).ToList();

        if (model.Splits.Count < 2)
            ModelState.AddModelError("", "A split must have at least 2 lines.");

        var total = model.Splits.Sum(s => s.Amount);
        if (Math.Abs(total - model.OriginalAmount) > 0.01m)
            ModelState.AddModelError("", $"Split amounts must sum to {model.OriginalAmount:C}. Current total: {total:C}.");

        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        var splits = model.Splits
            .Select(s => (s.Amount, s.Description, s.CategoryId))
            .ToList();

        var result = await _dataService.SplitTransactionAsync(model.TransactionId, splits);
        if (result.Count == 0)
        {
            TempData["Error"] = "Failed to split transaction.";
            return RedirectToReturnUrlOrIndex(model.ReturnUrl);
        }

        TempData["Message"] = $"Transaction split into {result.Count} parts.";
        return RedirectToReturnUrlOrIndex(model.ReturnUrl);
    }

    // Redirects back to the filtered/paged view the user came from when the URL is a
    // safe local path, falling back to a bare Index otherwise (open-redirect guard).
    private IActionResult RedirectToReturnUrlOrIndex(string? returnUrl, object? routeValues = null)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index), routeValues);
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
