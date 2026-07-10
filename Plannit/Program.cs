using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

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

var app = builder.Build();

// Configure the HTTP request pipeline.
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

app.UseRouting();

app.UseAuthorization();

app.Use(async (context, next) =>
{
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

    if (await db.Accounts.AnyAsync()) return;

    var today = DateOnly.FromDateTime(DateTime.Today);

    var checking = new Account { UserId = devUser.Id, Name = "Main Checking", Type = AccountType.Checking, Institution = "Chase" };
    var savings = new Account { UserId = devUser.Id, Name = "Emergency Fund", Type = AccountType.Savings, Institution = "Ally Bank" };
    var creditCard = new Account { UserId = devUser.Id, Name = "Visa Rewards", Type = AccountType.CreditCard, Institution = "Chase" };
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
