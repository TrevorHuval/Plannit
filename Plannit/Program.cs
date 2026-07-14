using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;
using Plannit.Services.Ai;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddSingleton<ICacheVersionProvider, CacheVersionProvider>();
builder.Services.AddMemoryCache();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 12;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddControllersWithViews();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>();

builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<NetWorthService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<SnapshotImportService>();
builder.Services.AddScoped<HoldingService>();
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
builder.Services.AddScoped<ImportWorkflowService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<BillService>();
builder.Services.AddScoped<ForecastService>();
builder.Services.AddScoped<SavingsGoalService>();
builder.Services.AddScoped<LoanService>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHostedService<MaintenanceBackgroundService>();
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

        if (builder.Configuration.GetValue<bool>("ForwardedHeaders:TrustProxyNetwork"))
        {
            // Opt-in only: trusts forwarded headers from any network. Use when the
            // proxy's IP can't be pinned ahead of time (e.g. some managed load balancers).
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }
        else
        {
            foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            {
                if (IPAddress.TryParse(proxy, out var ip))
                    options.KnownProxies.Add(ip);
            }
        }
    });
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
        }
        await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", token);
    };

    options.GlobalLimiter = RateLimiterConfiguration.CreateGlobalLimiter();
});

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

app.UseRateLimiter();

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

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/healthz"))
    {
        await next();
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Plannit.RequestLogging");
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        var failedUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        logger.LogError(ex, "Unhandled exception on {Method} {Path} (user {UserId}) after {ElapsedMs}ms",
            context.Request.Method, context.Request.Path, failedUserId, stopwatch.ElapsedMilliseconds);
        throw;
    }

    stopwatch.Stop();
    var requestUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
    logger.LogInformation("{Method} {Path} responded {StatusCode} in {ElapsedMs}ms (user {UserId})",
        context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds, requestUserId);
});

app.MapHealthChecks("/healthz").AllowAnonymous();

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

    if (!await db.Accounts.AnyAsync(a => a.Type == AccountType.Mortgage))
    {
        var mortgage = new Account
        {
            UserId = devUser.Id, Name = "Home Mortgage", Type = AccountType.Mortgage, Institution = "Wells Fargo",
            InterestRate = 0.0625m, MinimumPayment = 1850m, OriginalPrincipal = 320000m
        };
        var carLoan = new Account
        {
            UserId = devUser.Id, Name = "Car Loan", Type = AccountType.Loan, Institution = "Toyota Financial",
            InterestRate = 0.069m, MinimumPayment = 320m, OriginalPrincipal = 18000m
        };
        db.Accounts.AddRange(mortgage, carLoan);
        await db.SaveChangesAsync();

        var debtSnapshots = new List<BalanceSnapshot>();
        for (int i = 12; i >= 0; i--)
        {
            var date = today.AddMonths(-i);
            var month = 12 - i;
            debtSnapshots.Add(new BalanceSnapshot { AccountId = mortgage.Id, Date = date, Balance = 320000m - month * 900m });
            debtSnapshots.Add(new BalanceSnapshot { AccountId = carLoan.Id, Date = date, Balance = 18000m - month * 683m });
        }
        db.BalanceSnapshots.AddRange(debtSnapshots);
        await db.SaveChangesAsync();
    }

    if (!await db.SavingsGoals.AnyAsync())
    {
        var savingsAccount = await db.Accounts.FirstOrDefaultAsync(a => a.Type == AccountType.Savings);
        db.SavingsGoals.AddRange(
            new SavingsGoal { UserId = devUser.Id, Name = "House Down Payment", TargetAmount = 25000m, TargetDate = today.AddMonths(18), LinkedAccountId = savingsAccount?.Id },
            new SavingsGoal { UserId = devUser.Id, Name = "Vacation Fund", TargetAmount = 3000m, TargetDate = today.AddMonths(6), ManualProgress = 1200m }
        );
        await db.SaveChangesAsync();
    }
}

// Exposed so the integration test project (WebApplicationFactory<Program>) can boot the app.
public partial class Program;
