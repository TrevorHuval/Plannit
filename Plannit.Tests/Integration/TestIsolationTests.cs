using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;

namespace Plannit.Tests.Integration;

/// <summary>
/// Regression tests for audit item P2-07: every factory must run against its own throwaway
/// database and never against the developer's plannit.db (in source or in build output).
/// </summary>
public class TestIsolationTests
{
    [Fact]
    public void Factory_UsesItsOwnThrowawayDatabase()
    {
        using var factory = new PlannitWebAppFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dataSource = db.Database.GetDbConnection().DataSource;

        Assert.Equal(factory.DatabasePath, dataSource);
        Assert.NotEqual("plannit.db", Path.GetFileName(dataSource));
        Assert.StartsWith(Path.GetTempPath(), dataSource);
    }

    [Fact]
    public async Task TwoFactories_CannotSeeEachOthersUsers()
    {
        using var first = new PlannitWebAppFactory();
        using var second = new PlannitWebAppFactory();
        const string email = "isolation-shared@example.invalid";

        using var client = first.CreateClient();
        await HttpTestHelpers.RegisterAsync(client, email);

        using var firstScope = first.Services.CreateScope();
        using var secondScope = second.Services.CreateScope();
        var firstUsers = firstScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var secondUsers = secondScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        Assert.NotNull(await firstUsers.FindByEmailAsync(email));
        Assert.Null(await secondUsers.FindByEmailAsync(email));
    }

    [Fact]
    public async Task FixedEmail_CanBeRegisteredInEveryFreshFactory()
    {
        // The same fixed address twice in a row: the second run must not collide with the first.
        const string email = "isolation-fixed@example.invalid";
        for (var run = 0; run < 2; run++)
        {
            using var factory = new PlannitWebAppFactory();
            using var client = factory.CreateClientNoRedirect();
            var resp = await HttpTestHelpers.PostRegisterAsync(client, email);
            Assert.True(resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
                $"Run {run + 1}: registration returned {(int)resp.StatusCode}.");
        }
    }

    [Fact]
    public void DeveloperDatabase_IsNotCopiedIntoBuildOutput()
    {
        // Plannit.csproj used to copy plannit.db to the output directory, so the test host (and
        // any published artifact) carried the developer's financial data.
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "plannit.db")),
            "plannit.db was found next to the test binaries.");
    }

    [Fact]
    public void ThrowawayDatabase_IsDeletedOnDispose()
    {
        string path;
        using (var factory = new PlannitWebAppFactory())
        {
            using var scope = factory.Services.CreateScope();
            Assert.True(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.CanConnect());
            path = factory.DatabasePath;
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }
}
