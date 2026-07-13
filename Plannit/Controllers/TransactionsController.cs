using System.Security.Claims;
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
    private readonly CategorizationService _categorizationService;
    private readonly DataManagementService _dataService;
    private readonly AiSettingsService _aiSettings;
    private readonly ImportWorkflowService _importWorkflow;
    private readonly AuditService _audit;

    public TransactionsController(
        TransactionService transactionService,
        AccountService accountService,
        CategorizationService categorizationService,
        DataManagementService dataService,
        AiSettingsService aiSettings,
        ImportWorkflowService importWorkflow,
        AuditService audit)
    {
        _transactionService = transactionService;
        _accountService = accountService;
        _categorizationService = categorizationService;
        _dataService = dataService;
        _aiSettings = aiSettings;
        _importWorkflow = importWorkflow;
        _audit = audit;
    }

    // TempData key holding the serialized multi-file import state between requests.
    private const string WorkflowStateKey = "ImportWorkflowState";

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

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

        var created = await _transactionService.CreateAsync(model.AccountId, model.Date, model.Amount, model.Description);
        if (created is null) return NotFound();

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

        var step = await _importWorkflow.StartAsync(model.AccountId, account.Name, model.Files, model.PositionsStatement);
        return RenderStep(step);
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

        // The account is validated when the upload starts, but this confirm step is a
        // separate POST whose AccountId must be re-checked against the current user.
        var account = await _accountService.GetByIdAsync(model.AccountId);
        if (account is null) return NotFound();

        if (string.IsNullOrEmpty(model.AmountColumn) &&
            (string.IsNullOrEmpty(model.DebitColumn) || string.IsNullOrEmpty(model.CreditColumn)))
        {
            ModelState.AddModelError("", "Provide either an Amount column or both Debit and Credit columns.");
        }

        if (!ModelState.IsValid)
        {
            var preview = _importWorkflow.ReadCsvPreview(model.TempFileId);
            if (preview is not null)
            {
                model.AvailableColumns = preview.Value.Headers;
                model.PreviewRows = preview.Value.PreviewRows;
            }
            return View("MapColumns", model);
        }

        var step = await _importWorkflow.ConfirmCsvMapAsync(model, account.Name, ReadWorkflowState());
        return RenderStep(step);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmSnapshotImport(SnapshotConfirmViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("ConfirmSnapshot", model);
        }

        if (await _accountService.GetByIdAsync(model.AccountId) is null) return NotFound();

        var step = await _importWorkflow.ConfirmSnapshotAsync(model, ReadWorkflowState());
        return RenderStep(step);
    }

    // Reads the multi-file import state persisted across the confirm/map round-trip.
    // Returns a fresh (empty) state for a single-file flow that had nothing queued.
    private ImportWorkflowState ReadWorkflowState()
    {
        if (TempData[WorkflowStateKey] is not string json || string.IsNullOrEmpty(json))
            return new ImportWorkflowState();
        return JsonSerializer.Deserialize<ImportWorkflowState>(json) ?? new ImportWorkflowState();
    }

    // Translates a workflow step into an MVC result: render the next confirm screen
    // (persisting remaining state), the combined result page, or a redirect on expiry.
    private IActionResult RenderStep(ImportStep step)
    {
        switch (step)
        {
            case ShowPendingImportStep show:
                TempData[WorkflowStateKey] = JsonSerializer.Serialize(show.State);
                return View(show.ViewName, show.Model);
            case FinalizeImportStep finalize:
                return View("MultiImportResult", finalize.Result);
            case ImportExpiredStep expired:
                TempData["Error"] = expired.Message;
                return RedirectToAction(nameof(Import));
            default:
                throw new InvalidOperationException($"Unknown import step: {step.GetType().Name}");
        }
    }

    public async Task<IActionResult> ExportCsv(int? accountId, DateOnly? startDate, DateOnly? endDate, string? searchText, int? categoryId)
    {
        searchText = SanitizeSearchText(searchText);
        var (items, _) = await _transactionService.GetFilteredAsync(accountId, startDate, endDate, searchText, categoryId, 1, int.MaxValue);

        var sb = new StringBuilder();
        sb.AppendLine("Date,Description,Amount,Account,Category,Notes");
        foreach (var t in items)
        {
            var desc = CsvEscapeText(t.Description);
            var acct = CsvEscapeText(t.Account.Name);
            var cat = CsvEscapeText(t.Category?.Name ?? "");
            var notes = CsvEscapeText(t.Notes ?? "");
            sb.AppendLine($"{t.Date:yyyy-MM-dd},{desc},{t.Amount},{acct},{cat},{notes}");
        }

        await _audit.LogAsync(UserId, "DataExport", $"CSV export ({items.Count} transactions)", HttpContext.Connection.RemoteIpAddress?.ToString());
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
        if (count > 0)
            await _audit.LogAsync(UserId, "BulkDelete", $"Deleted {count} transaction(s)", HttpContext.Connection.RemoteIpAddress?.ToString());
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

    // Text cells only (never amounts/dates): a leading =, +, -, @, tab, or CR would be
    // executed as a formula by Excel/Sheets, so neutralize it with a quote prefix.
    internal static string CsvEscapeText(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return CsvEscape(value);
    }
}
