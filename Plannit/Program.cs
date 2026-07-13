using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;
using Plannit.Services.Ai;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddControllersWithViews();

builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<NetWorthService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<SnapshotImportService>();
builder.Services.AddScoped<CsvImportService>();
builder.Services.AddScoped<OfxImportService>();
builder.Services.AddScoped<PositionsCsvImportService>();
builder.Services.AddScoped<PdfStatementService>();
builder.Services.AddScoped<CategorizationService>();
builder.Services.AddScoped<ReportsService>();
builder.Services.AddScoped<ProjectionService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<RecurringDetectionService>();
builder.Services.AddScoped<DataManagementService>();
builder.Services.AddSingleton<ClaudeCliStatus>();
builder.Services.AddScoped<AiSettingsService>();
builder.Services.AddScoped<SmartCategorizationService>();
builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromSeconds(120));

var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrEmpty(dataProtectionKeyPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
        .SetApplicationName("Plannit");
}

if (builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Clickjacking/MIME-sniffing/CSP defense-in-depth. script-src/style-src need
// 'unsafe-inline' because views use inline Chart.js blocks and the dark-mode
// FOUC-prevention script; all other sources are locked to self (assets are vendored).
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Frame-Options"] = "DENY";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

app.UseRouting();

app.UseAuthorization();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/Identity/Account/Register"))
    {
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var allowRegistration = config.GetValue("AllowRegistration", true);
        if (!allowRegistration)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Registration is disabled.");
            return;
        }
    }

    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is not null)
    {
        var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(userId);
    }
    await next();
});

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

if (!app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

await app.Services.GetRequiredService<ClaudeCliStatus>().DetectAsync();

await RepairLiabilitySnapshotSignsAsync(app.Services, app.Logger);

CleanupOldTempUploads(app.Environment.ContentRootPath);

if (app.Environment.IsDevelopment())
{
    await SeedDevDataAsync(app.Services);
}

app.Run();

static async Task SeedDevDataAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

    var devUser = await userManager.FindByEmailAsync("dev@finplanner.local");
    if (devUser is null) return;

    db.SetCurrentUser(devUser.Id);

    var today = DateOnly.FromDateTime(DateTime.Today);

    Account checking, creditCard;

    if (!await db.Accounts.AnyAsync())
    {
        checking = new Account { UserId = devUser.Id, Name = "Main Checking", Type = AccountType.Checking, Institution = "Chase" };
        var savings = new Account { UserId = devUser.Id, Name = "Emergency Fund", Type = AccountType.Savings, Institution = "Ally Bank" };
        creditCard = new Account { UserId = devUser.Id, Name = "Visa Rewards", Type = AccountType.CreditCard, Institution = "Chase" };
        var roth = new Account { UserId = devUser.Id, Name = "Roth IRA", Type = AccountType.RothIra, Institution = "Fidelity" };
        var k401 = new Account { UserId = devUser.Id, Name = "Company 401(k)", Type = AccountType.Retirement401k, Institution = "Vanguard" };
        var brokerage = new Account { UserId = devUser.Id, Name = "Brokerage", Type = AccountType.Brokerage, Institution = "Fidelity" };

        db.Accounts.AddRange(checking, savings, creditCard, roth, k401, brokerage);
        await db.SaveChangesAsync();

        var snapshots = new List<BalanceSnapshot>();
        for (int i = 12; i >= 0; i--)
        {
            var date = today.AddMonths(-i);
            var month = 12 - i;

            snapshots.Add(new BalanceSnapshot { AccountId = checking.Id, Date = date, Balance = 3200m + month * 50m });
            snapshots.Add(new BalanceSnapshot { AccountId = savings.Id, Date = date, Balance = 15000m + month * 400m });
            snapshots.Add(new BalanceSnapshot { AccountId = creditCard.Id, Date = date, Balance = 1800m - month * 30m });
            snapshots.Add(new BalanceSnapshot { AccountId = roth.Id, Date = date, Balance = 42000m + month * 900m });
            snapshots.Add(new BalanceSnapshot { AccountId = k401.Id, Date = date, Balance = 85000m + month * 1500m });
            snapshots.Add(new BalanceSnapshot { AccountId = brokerage.Id, Date = date, Balance = 12000m + month * 350m });
        }

        db.BalanceSnapshots.AddRange(snapshots);
        await db.SaveChangesAsync();
    }
    else
    {
        checking = await db.Accounts.FirstAsync(a => a.Type == AccountType.Checking);
        creditCard = await db.Accounts.FirstAsync(a => a.Type == AccountType.CreditCard);
    }

    var categorizationService = scope.ServiceProvider.GetRequiredService<CategorizationService>();
    await categorizationService.EnsureDefaultCategoriesAsync(devUser.Id);

    if (!await db.Transactions.AnyAsync())
    {
        var txns = new List<Transaction>();
        var descriptions = new[]
        {
            ("Whole Foods Market", -87.43m), ("Shell Gas Station", -45.12m), ("Netflix", -15.99m),
            ("Target", -62.30m), ("Starbucks", -5.75m), ("Electric Company", -142.00m),
            ("Water Utility", -38.50m), ("Amazon.com", -29.99m), ("Kroger", -110.25m),
            ("Uber Eats", -22.80m), ("Spotify", -9.99m), ("AT&T Wireless", -85.00m),
            ("Payroll Deposit", 3200.00m), ("Interest Payment", 2.15m)
        };
        var rng = new Random(42);

        for (int month = 0; month < 3; month++)
        {
            foreach (var (desc, baseAmt) in descriptions)
            {
                var date = today.AddMonths(-month).AddDays(-rng.Next(0, 28));
                var acctId = baseAmt > 0 ? checking.Id : (rng.Next(2) == 0 ? checking.Id : creditCard.Id);
                var variation = baseAmt * (1 + (rng.Next(-10, 11) / 100m));
                var amount = Math.Round(variation, 2);

                txns.Add(new Transaction
                {
                    AccountId = acctId,
                    Date = date,
                    Amount = amount,
                    Description = desc,
                    OriginalDescription = desc,
                    ImportHash = CsvImportService.ComputeImportHash(acctId, date, amount, desc)
                });
            }
        }

        db.Transactions.AddRange(txns);
        await db.SaveChangesAsync();

        await categorizationService.ApplyRulesToUncategorizedAsync();
    }
}

static async Task RepairLiabilitySnapshotSignsAsync(IServiceProvider services, ILogger logger)
{
    using var scope = services.CreateScope();
    var accountService = scope.ServiceProvider.GetRequiredService<AccountService>();
    var repaired = await accountService.RepairLiabilitySnapshotSignsAsync();
    if (repaired > 0)
        logger.LogInformation("Repaired sign on {Count} liability balance snapshot(s).", repaired);
}

static void CleanupOldTempUploads(string contentRootPath)
{
    var tempDir = Path.Combine(contentRootPath, "TempUploads");
    if (!Directory.Exists(tempDir)) return;

    var cutoff = DateTime.UtcNow.AddHours(-24);
    foreach (var file in Directory.GetFiles(tempDir))
    {
        if (File.GetCreationTimeUtc(file) < cutoff)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
