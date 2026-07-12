using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class TransactionsController : Controller
{
    private readonly TransactionService _transactionService;
    private readonly AccountService _accountService;
    private readonly CsvImportService _csvImportService;
    private readonly OfxImportService _ofxImportService;
    private readonly CategorizationService _categorizationService;
    private readonly string _tempUploadPath;

    public TransactionsController(
        TransactionService transactionService,
        AccountService accountService,
        CsvImportService csvImportService,
        OfxImportService ofxImportService,
        CategorizationService categorizationService,
        IWebHostEnvironment env)
    {
        _transactionService = transactionService;
        _accountService = accountService;
        _csvImportService = csvImportService;
        _ofxImportService = ofxImportService;
        _categorizationService = categorizationService;
        _tempUploadPath = Path.Combine(env.ContentRootPath, "TempUploads");
    }

    public async Task<IActionResult> Index(int? accountId, DateOnly? startDate, DateOnly? endDate, string? searchText, int? categoryId, int page = 1)
    {
        const int pageSize = 50;
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
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
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
                CategoryName = t.Category?.Name
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

    public async Task<IActionResult> Edit(int id)
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
            Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList()
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

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await _transactionService.DeleteAsync(id);
        return RedirectToAction(nameof(Index));
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
            if (ext is not ".csv" and not ".ofx" and not ".qfx")
            {
                ModelState.AddModelError(nameof(model.Files), $"Unsupported file format: {file.FileName}. Supported: CSV, OFX, QFX.");
                var accounts = await _accountService.GetAllAsync();
                model.Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
                return View(model);
            }
        }

        var multiResult = new MultiImportResultViewModel { AccountName = account.Name };
        var csvsPendingMapping = new List<CsvPendingMapViewModel>();

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
            else
            {
                var profile = await _csvImportService.GetProfileAsync(model.AccountId);
                if (profile is not null)
                {
                    using var stream = file.OpenReadStream();
                    var result = await _csvImportService.ImportAsync(stream, model.AccountId, file.FileName,
                        profile.DateColumn, profile.DateFormat,
                        profile.AmountColumn, profile.DebitColumn, profile.CreditColumn,
                        profile.DescriptionColumn);
                    result.AccountName = account.Name;
                    multiResult.FileResults.Add(result);
                }
                else
                {
                    Directory.CreateDirectory(_tempUploadPath);
                    var tempId = Guid.NewGuid().ToString();
                    var tempPath = Path.Combine(_tempUploadPath, tempId + ".csv");
                    using (var stream = new FileStream(tempPath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    csvsPendingMapping.Add(new CsvPendingMapViewModel
                    {
                        FileName = file.FileName,
                        TempFileId = tempId,
                        AccountId = model.AccountId,
                        AccountName = account.Name
                    });
                }
            }
        }

        if (csvsPendingMapping.Count > 0)
        {
            if (multiResult.FileResults.Count > 0)
            {
                var categorized = await _categorizationService.ApplyRulesToUncategorizedAsync();
                multiResult.CategorizedCount = categorized;
            }

            multiResult.CsvsPendingMapping = csvsPendingMapping;
            TempData["MultiImportResults"] = System.Text.Json.JsonSerializer.Serialize(multiResult.FileResults);
            TempData["MultiImportAccountName"] = account.Name;

            var first = csvsPendingMapping[0];
            var remainingJson = csvsPendingMapping.Count > 1
                ? System.Text.Json.JsonSerializer.Serialize(csvsPendingMapping.Skip(1).ToList())
                : null;
            if (remainingJson != null)
                TempData["RemainingCsvs"] = remainingJson;

            using var readStream = new FileStream(Path.Combine(_tempUploadPath, first.TempFileId + ".csv"), FileMode.Open, FileAccess.Read);
            var (headers, previewRows) = _csvImportService.ReadPreview(readStream);

            var mapVm = new ImportMapViewModel
            {
                AccountId = first.AccountId,
                AccountName = first.AccountName,
                FileName = first.FileName,
                TempFileId = first.TempFileId,
                AvailableColumns = headers,
                PreviewRows = previewRows
            };

            return View("MapColumns", mapVm);
        }

        if (multiResult.FileResults.Any(r => r.ImportedCount > 0))
        {
            var categorized = await _categorizationService.ApplyRulesToUncategorizedAsync();
            multiResult.CategorizedCount = categorized;
        }

        return View("MultiImportResult", multiResult);
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
            var tempPath = Path.Combine(_tempUploadPath, model.TempFileId + ".csv");
            if (System.IO.File.Exists(tempPath))
            {
                using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var (headers, rows) = _csvImportService.ReadPreview(stream);
                model.AvailableColumns = headers;
                model.PreviewRows = rows;
            }
            return View("MapColumns", model);
        }

        var tempFilePath = Path.Combine(_tempUploadPath, model.TempFileId + ".csv");
        if (!System.IO.File.Exists(tempFilePath))
        {
            TempData["Error"] = "Upload expired. Please re-upload the file.";
            return RedirectToAction(nameof(Import));
        }

        await _csvImportService.SaveProfileAsync(model.AccountId, model.DateColumn, model.DateFormat,
            model.AmountColumn, model.DebitColumn, model.CreditColumn, model.DescriptionColumn);

        ImportResultViewModel result;
        using (var stream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read))
        {
            result = await _csvImportService.ImportAsync(stream, model.AccountId, model.FileName,
                model.DateColumn, model.DateFormat, model.AmountColumn, model.DebitColumn,
                model.CreditColumn, model.DescriptionColumn);
        }

        try { System.IO.File.Delete(tempFilePath); } catch { }

        var account = await _accountService.GetByIdAsync(model.AccountId);
        result.AccountName = account?.Name ?? "Unknown";

        var priorResultsJson = TempData["MultiImportResults"] as string;
        var remainingCsvsJson = TempData["RemainingCsvs"] as string;
        var multiAccountName = TempData["MultiImportAccountName"] as string;

        var allResults = new List<ImportResultViewModel>();
        if (priorResultsJson != null)
        {
            var prior = System.Text.Json.JsonSerializer.Deserialize<List<ImportResultViewModel>>(priorResultsJson);
            if (prior != null) allResults.AddRange(prior);
        }
        allResults.Add(result);

        if (!string.IsNullOrEmpty(remainingCsvsJson))
        {
            var remaining = System.Text.Json.JsonSerializer.Deserialize<List<CsvPendingMapViewModel>>(remainingCsvsJson);
            if (remaining != null && remaining.Count > 0)
            {
                TempData["MultiImportResults"] = System.Text.Json.JsonSerializer.Serialize(allResults);
                TempData["MultiImportAccountName"] = multiAccountName;

                var next = remaining[0];
                if (remaining.Count > 1)
                    TempData["RemainingCsvs"] = System.Text.Json.JsonSerializer.Serialize(remaining.Skip(1).ToList());

                var nextTempPath = Path.Combine(_tempUploadPath, next.TempFileId + ".csv");
                if (!System.IO.File.Exists(nextTempPath))
                {
                    TempData["Error"] = "Upload expired for " + next.FileName + ". Please re-upload.";
                    return RedirectToAction(nameof(Import));
                }

                using var readStream = new FileStream(nextTempPath, FileMode.Open, FileAccess.Read);
                var (headers, previewRows) = _csvImportService.ReadPreview(readStream);

                var profile = await _csvImportService.GetProfileAsync(next.AccountId);

                var mapVm = new ImportMapViewModel
                {
                    AccountId = next.AccountId,
                    AccountName = next.AccountName,
                    FileName = next.FileName,
                    TempFileId = next.TempFileId,
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
                }

                return View("MapColumns", mapVm);
            }
        }

        var categorized = 0;
        if (allResults.Any(r => r.ImportedCount > 0))
        {
            categorized = await _categorizationService.ApplyRulesToUncategorizedAsync();
        }

        var multiResult = new MultiImportResultViewModel
        {
            AccountName = multiAccountName ?? result.AccountName,
            FileResults = allResults,
            CategorizedCount = categorized
        };

        return View("MultiImportResult", multiResult);
    }
}
