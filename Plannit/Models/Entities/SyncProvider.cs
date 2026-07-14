namespace Plannit.Models.Entities;

/// <summary>
/// Which automated bank-aggregation provider backs a <see cref="SyncConnection"/>.
/// Only <see cref="SimpleFin"/> exists today; the enum leaves room for a second provider
/// (e.g. Plaid) without a schema change.
/// </summary>
public enum SyncProvider
{
    SimpleFin = 0
}
