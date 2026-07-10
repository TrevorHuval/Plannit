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
    private readonly string _tempUploadPath;

    public TransactionsController(
        TransactionService transactionService,
        AccountService accountService,
        CsvImportService csvImportService,
        IWebHostEnvironment env)
    {
        _transactionService = transactionService;
        _accountService = accountService;
        _csvImportService = csvImportService;
        _tempUploadPath = Path.Combine(env.ContentRootPath, "TempUploads");
    }

    public async Task<IActionResult> Index(int? accountId, DateOnly? startDate, DateOnly? endDate, string? searchText, int page = 1)
    {
        const int pageSize = 50;
        var (items, totalCount) = await _transactionService.GetFilteredAsync(accountId, startDate, endDate, searchText, page, pageSize);
        var accounts = await _accountService.GetAllAsync();

        var vm = new TransactionListViewModel
        {
            AccountId = accountId,
            StartDate = startDate,
            EndDate = endDate,
            SearchText = searchText,
            Page = page,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList(),
            Transactions = items.Select(t => new TransactionRowViewModel
            {
                Id = t.Id,
                Date = t.Date,
                Amount = t.Amount,
                Description = t.Description,
                AccountName = t.Account.Name,
                AccountType = t.Account.Type
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
    public async Task<IActionResult> Import(ImportUploadViewModel model)
    {
        if (model.File is null || model.File.Length == 0)
        {
            ModelState.AddModelError(nameof(model.File), "Please select a CSV file.");
        }

        if (!ModelState.IsValid)
        {
            var accounts = await _accountService.GetAllAsync();
            model.Accounts = accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
            return View(model);
        }

        Directory.CreateDirectory(_tempUploadPath);
        var tempId = Guid.NewGuid().ToString();
        var tempPath = Path.Combine(_tempUploadPath, tempId + ".csv");

        using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await model.File!.CopyToAsync(stream);
        }

        using var readStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
        var (headers, previewRows) = _csvImportService.ReadPreview(readStream);

        var account = await _accountService.GetByIdAsync(model.AccountId);
        if (account is null) return NotFound();

        var profile = await _csvImportService.GetProfileAsync(model.AccountId);

        var mapVm = new ImportMapViewModel
        {
            AccountId = model.AccountId,
            AccountName = account.Name,
            FileName = model.File!.FileName,
            TempFileId = tempId,
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmImport(ImportMapViewModel model)
    {
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

        return View("ImportResult", result);
    }
}
