using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly DataManagementService _dataService;

    public SettingsController(DataManagementService dataService)
    {
        _dataService = dataService;
    }

    public IActionResult Index()
    {
        return View();
    }

    public async Task<IActionResult> Imports()
    {
        var batches = await _dataService.GetImportBatchesAsync();
        return View(batches);
    }

    public async Task<IActionResult> ImportDetail(int id)
    {
        var batch = await _dataService.GetImportBatchAsync(id);
        if (batch is null) return NotFound();

        ViewBag.EditedCount = await _dataService.GetBatchEditedCountAsync(id);
        return View(batch);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoImport(int id)
    {
        var (success, message) = await _dataService.UndoImportBatchAsync(id);
        TempData[success ? "Message" : "Error"] = message;
        return RedirectToAction(nameof(Imports));
    }

    public async Task<IActionResult> ExportJson()
    {
        var json = await _dataService.ExportFullBackupJsonAsync();
        var bytes = Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json", $"plannit-backup-{DateTime.UtcNow:yyyy-MM-dd}.json");
    }
}
