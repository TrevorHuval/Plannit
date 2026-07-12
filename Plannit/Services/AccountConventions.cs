using Plannit.Models.Entities;

namespace Plannit.Services;

public static class AccountConventions
{
    public static decimal NormalizeSnapshotBalance(AccountType type, decimal value) =>
        NetWorthService.IsLiability(type) ? Math.Abs(value) : value;

    public static decimal SignedBalance(AccountType type, decimal value) =>
        NetWorthService.IsLiability(type) ? -value : value;
}
