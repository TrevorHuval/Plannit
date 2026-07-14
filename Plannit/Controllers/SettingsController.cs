using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;
using Plannit.Services.Ai;

namespace Plannit.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly DataManagementService _dataService;
    private readonly AiSettingsService _aiSettings;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;
    private readonly IEmailSender _emailSender;

    public SettingsController(DataManagementService dataService, AiSettingsService aiSettings, AuditService audit, NotificationService notifications, IEmailSender emailSender)
    {
        _dataService = dataService;
        _aiSettings = aiSettings;
        _audit = audit;
        _notifications = notifications;
        _emailSender = emailSender;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index()
    {
        ViewBag.AiConfigured = await _aiSettings.IsConfiguredAsync();
        return View();
    }

    public async Task<IActionResult> AuditLog(DateOnly? startDate, DateOnly? endDate)
    {
        ViewBag.StartDate = startDate;
        ViewBag.EndDate = endDate;
        var events = await _audit.GetRecentAsync(startDate, endDate);
        return View(events);
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
        if (success)
            await _audit.LogAsync(UserId, "ImportUndo", message, HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData[success ? "Message" : "Error"] = message;
        return RedirectToAction(nameof(Imports));
    }

    public async Task<IActionResult> ExportJson()
    {
        var json = await _dataService.ExportFullBackupJsonAsync();
        await _audit.LogAsync(UserId, "DataExport", "Full JSON backup", HttpContext.Connection.RemoteIpAddress?.ToString());
        var bytes = Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json", $"plannit-backup-{DateTime.UtcNow:yyyy-MM-dd}.json");
    }

    // ===== AI Smart Categorization settings =====

    public async Task<IActionResult> Ai()
    {
        var vm = await BuildAiViewModelAsync();
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAi(AiSettingsViewModel vm)
    {
        if (vm.Provider == AiProvider.ClaudeCli && !_aiSettings.ClaudeCliAvailable)
            ModelState.AddModelError(nameof(vm.Provider), "The Claude CLI is not available on this machine.");
        if (vm.Provider == AiProvider.OpenAiCompatible && string.IsNullOrWhiteSpace(vm.Endpoint))
            ModelState.AddModelError(nameof(vm.Endpoint), "A base URL is required for an OpenAI-compatible provider.");
        if (vm.Provider is AiProvider.AnthropicApi or AiProvider.OpenAiCompatible
            && string.IsNullOrWhiteSpace(vm.Model))
            ModelState.AddModelError(nameof(vm.Model), "A model name is required.");

        if (!ModelState.IsValid)
        {
            var reloaded = await BuildAiViewModelAsync();
            reloaded.Provider = vm.Provider;
            reloaded.Endpoint = vm.Endpoint;
            reloaded.Model = vm.Model;
            return View("Ai", reloaded);
        }

        await _aiSettings.SaveAsync(UserId, vm.Provider, vm.Endpoint, vm.Model, vm.ApiKey);
        await _audit.LogAsync(UserId, "AiSettingsChanged", $"Provider set to {vm.Provider}", HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "AI settings saved.";
        return RedirectToAction(nameof(Ai));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection()
    {
        var settings = await _aiSettings.GetAsync();
        var vm = await BuildAiViewModelAsync();

        var categorizer = settings is null ? null : _aiSettings.Create(settings);
        if (categorizer is null)
        {
            vm.TestSucceeded = false;
            vm.TestResult = "No usable provider is configured. Save your settings first.";
            return View("Ai", vm);
        }

        var (ok, message) = await categorizer.TestConnectionAsync();
        vm.TestSucceeded = ok;
        vm.TestResult = message;
        return View("Ai", vm);
    }

    // ===== Notification settings =====

    public async Task<IActionResult> Notifications()
    {
        var vm = await BuildNotificationViewModelAsync();
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNotifications(NotificationSettingsViewModel vm)
    {
        if (vm.EmailEnabled && string.IsNullOrWhiteSpace(vm.Email))
            ModelState.AddModelError(nameof(vm.Email), "An email address is required to enable email alerts.");

        if (!ModelState.IsValid)
        {
            vm.SmtpConfigured = _emailSender.IsConfigured;
            return View("Notifications", vm);
        }

        await _notifications.SavePreferencesAsync(
            UserId, vm.Email, vm.EmailEnabled, vm.DigestMode,
            vm.BudgetOverageEnabled, vm.BillDueEnabled, vm.LowForecastBalanceEnabled, vm.LargeTransactionEnabled, vm.StaleAccountEnabled);
        await _audit.LogAsync(UserId, "NotificationSettingsChanged", null, HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData["Message"] = "Notification settings saved.";
        return RedirectToAction(nameof(Notifications));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTestEmail()
    {
        var vm = await BuildNotificationViewModelAsync();

        if (string.IsNullOrWhiteSpace(vm.Email))
        {
            vm.TestSucceeded = false;
            vm.TestResult = "Enter and save an email address first.";
            return View("Notifications", vm);
        }

        var (ok, message) = await _notifications.SendTestEmailAsync(vm.Email);
        vm.TestSucceeded = ok;
        vm.TestResult = message;
        return View("Notifications", vm);
    }

    private async Task<NotificationSettingsViewModel> BuildNotificationViewModelAsync()
    {
        var prefs = await _notifications.GetPreferencesAsync(UserId);
        return new NotificationSettingsViewModel
        {
            Email = prefs.Email,
            EmailEnabled = prefs.EmailEnabled,
            DigestMode = prefs.DigestMode,
            BudgetOverageEnabled = prefs.BudgetOverageEnabled,
            BillDueEnabled = prefs.BillDueEnabled,
            LowForecastBalanceEnabled = prefs.LowForecastBalanceEnabled,
            LargeTransactionEnabled = prefs.LargeTransactionEnabled,
            StaleAccountEnabled = prefs.StaleAccountEnabled,
            SmtpConfigured = _emailSender.IsConfigured
        };
    }

    private async Task<AiSettingsViewModel> BuildAiViewModelAsync()
    {
        var settings = await _aiSettings.GetAsync();
        return new AiSettingsViewModel
        {
            Provider = settings?.Provider ?? AiProvider.None,
            Endpoint = settings?.Endpoint,
            Model = settings?.Model,
            HasStoredKey = !string.IsNullOrEmpty(settings?.ApiKeyProtected),
            ClaudeCliAvailable = _aiSettings.ClaudeCliAvailable,
            ClaudeCliVersion = _aiSettings.ClaudeCliVersion
        };
    }
}
