using System.ComponentModel.DataAnnotations;
using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class SyncIndexViewModel
{
    public List<SyncConnection> Connections { get; set; } = new();
}

public class SyncConnectViewModel
{
    [Required(ErrorMessage = "Paste the setup token you copied from SimpleFIN.")]
    [Display(Name = "Setup token")]
    public string SetupToken { get; set; } = "";
}

public class SyncMapViewModel
{
    public int ConnectionId { get; set; }
    public string ConnectionName { get; set; } = "";
    public List<SyncMapRow> Rows { get; set; } = new();
    public List<Account> Accounts { get; set; } = new();
}

public class SyncMapRow
{
    public string ExternalAccountId { get; set; } = "";
    public string? ExternalAccountName { get; set; }
    public string? ExternalOrgName { get; set; }
    public int? AccountId { get; set; }
}

public class SyncLogsViewModel
{
    public SyncConnection Connection { get; set; } = null!;
    public List<SyncLog> Logs { get; set; } = new();
}
