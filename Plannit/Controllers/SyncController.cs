using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Plannit.Models.ViewModels;
using Plannit.Services;
using Plannit.Services.Sync;

namespace Plannit.Controllers;

/// <summary>
/// Automated bank sync (SimpleFIN). The whole controller is gated behind the
/// <c>BankSync:Enabled</c> feature flag — when off, every action 404s and the feature is hidden,
/// so file import remains the default path.
/// </summary>
[Authorize]
public class SyncController : Controller
{
    private readonly SyncService _sync;
    private readonly AccountService _accounts;
    private readonly AuditService _audit;
    private readonly IConfiguration _config;

    public SyncController(SyncService sync, AccountService accounts, AuditService audit, IConfiguration config)
    {
        _sync = sync;
        _accounts = accounts;
        _audit = audit;
        _config = config;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!SyncService.IsFeatureEnabled(_config))
            context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    public async Task<IActionResult> Index()
    {
        var vm = new SyncIndexViewModel { Connections = await _sync.GetConnectionsAsync() };
        return View(vm);
    }

    [HttpGet]
    public IActionResult Connect() => View(new SyncConnectViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Connect(SyncConnectViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var (ok, message, connectionId) = await _sync.ConnectAsync(UserId, vm.SetupToken);
        if (!ok)
        {
            ModelState.AddModelError(nameof(vm.SetupToken), message);
            return View(vm);
        }

        await _audit.LogAsync(UserId, "SyncConnected", "SimpleFIN bank connection linked", Ip);
        TempData["Message"] = message;
        return RedirectToAction(nameof(Map), new { id = connectionId });
    }

    [HttpGet]
    public async Task<IActionResult> Map(int id)
    {
        var connection = await _sync.GetConnectionAsync(id);
        if (connection is null) return NotFound();

        var vm = new SyncMapViewModel
        {
            ConnectionId = connection.Id,
            ConnectionName = connection.Name,
            Accounts = await _accounts.GetAllAsync(),
            Rows = connection.AccountMappings
                .OrderBy(m => m.ExternalOrgName).ThenBy(m => m.ExternalAccountName)
                .Select(m => new SyncMapRow
                {
                    ExternalAccountId = m.ExternalAccountId,
                    ExternalAccountName = m.ExternalAccountName,
                    ExternalOrgName = m.ExternalOrgName,
                    AccountId = m.AccountId
                })
                .ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Map(int id, SyncMapViewModel vm)
    {
        var map = vm.Rows.ToDictionary(r => r.ExternalAccountId, r => r.AccountId);
        var ok = await _sync.SaveMappingsAsync(id, map);
        if (!ok) return NotFound();

        await _audit.LogAsync(UserId, "SyncMappingsSaved", $"Connection {id}", Ip);
        TempData["Message"] = "Account mappings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(int id)
    {
        try
        {
            if (!await _sync.RefreshConnectionAccountsAsync(id)) return NotFound();
        }
        catch (SimpleFinAuthException)
        {
            TempData["Error"] = "SimpleFIN rejected the connection. Re-link it with a new setup token.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception)
        {
            TempData["Error"] = "Could not refresh accounts from SimpleFIN. Try again shortly.";
            return RedirectToAction(nameof(Map), new { id });
        }

        TempData["Message"] = "Account list refreshed.";
        return RedirectToAction(nameof(Map), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(int id)
    {
        var result = await _sync.SyncNowAsync(id);
        if (result is null) return NotFound();

        await _audit.LogAsync(UserId, "SyncNow", $"Connection {id}: {result.Message}", Ip);

        if (result.TokenExpired)
            TempData["Error"] = result.Message;
        else if (!result.Success)
            TempData["Error"] = result.Message;
        else
            TempData["Message"] = result.Message;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, bool active)
    {
        if (!await _sync.SetActiveAsync(id, active)) return NotFound();
        TempData["Message"] = active ? "Automatic sync enabled." : "Automatic sync paused.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await _sync.DeleteConnectionAsync(id)) return NotFound();
        await _audit.LogAsync(UserId, "SyncDeleted", $"Connection {id}", Ip);
        TempData["Message"] = "Bank connection removed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Logs(int id)
    {
        var connection = await _sync.GetConnectionAsync(id);
        if (connection is null) return NotFound();

        var vm = new SyncLogsViewModel
        {
            Connection = connection,
            Logs = await _sync.GetLogsAsync(id)
        };
        return View(vm);
    }
}
