using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class AccountConventionsTests
{
    [Theory]
    [InlineData(AccountType.CreditCard, -110.46, 110.46)]
    [InlineData(AccountType.CreditCard, 110.46, 110.46)]
    [InlineData(AccountType.CreditCard, 0, 0)]
    public void NormalizeSnapshotBalance_LiabilityAlwaysPositive(AccountType type, decimal input, decimal expected)
    {
        Assert.Equal(expected, AccountConventions.NormalizeSnapshotBalance(type, input));
    }

    [Theory]
    [InlineData(AccountType.Checking, 1234.56)]
    [InlineData(AccountType.Checking, -50)]
    [InlineData(AccountType.Brokerage, 9000)]
    public void NormalizeSnapshotBalance_AssetUnchanged(AccountType type, decimal input)
    {
        Assert.Equal(input, AccountConventions.NormalizeSnapshotBalance(type, input));
    }

    [Fact]
    public void SignedBalance_LiabilityIsNegated()
    {
        Assert.Equal(-1800m, AccountConventions.SignedBalance(AccountType.CreditCard, 1800m));
    }

    [Fact]
    public void SignedBalance_AssetIsUnchanged()
    {
        Assert.Equal(5000m, AccountConventions.SignedBalance(AccountType.Savings, 5000m));
    }
}
