using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class ProjectionsController : Controller
{
    private readonly ProjectionService _projectionService;

    public ProjectionsController(ProjectionService projectionService)
    {
        _projectionService = projectionService;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index()
    {
        var scenarios = await _projectionService.GetScenariosAsync();
        var vm = new ScenarioListViewModel
        {
            Scenarios = scenarios.Select(s => new ScenarioSummary
            {
                Id = s.Id,
                Name = s.Name,
                RetirementAge = s.RetirementAge,
                LifeExpectancy = s.LifeExpectancy,
                AnnualRetirementSpending = s.AnnualRetirementSpending,
                AccountCount = s.AccountAssumptions.Count
            }).ToList()
        };
        return View(vm);
    }

    public async Task<IActionResult> Create()
    {
        var accounts = await _projectionService.GetActiveAccountsAsync();
        var vm = BuildFormViewModel(null, accounts);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ScenarioFormViewModel vm)
    {
        if (!ModelState.IsValid)
            return View(vm);

        var scenario = new ProjectionScenario
        {
            UserId = UserId,
            Name = vm.Name,
            BirthYear = vm.BirthYear,
            RetirementAge = vm.RetirementAge,
            LifeExpectancy = vm.LifeExpectancy,
            AnnualRetirementSpending = vm.AnnualRetirementSpending,
            InflationRate = vm.InflationRate,
            ReturnStdDev = vm.ReturnStdDev,
            AccountAssumptions = vm.Accounts.Select(a => new ProjectionAccountAssumption
            {
                AccountId = a.AccountId,
                AnnualContribution = a.AnnualContribution,
                EmployerMatch = a.EmployerMatch,
                ExpectedReturnRate = a.ExpectedReturnRate,
                ContributionEndAge = a.ContributionEndAge
            }).ToList(),
            Events = vm.Events
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => new ProjectionEvent
                {
                    Name = e.Name,
                    Age = e.Age,
                    Amount = e.Amount,
                    IsRecurring = e.IsRecurring,
                    EndAge = e.EndAge
                }).ToList()
        };

        var id = await _projectionService.SaveScenarioAsync(scenario);
        return RedirectToAction(nameof(Results), new { id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var scenario = await _projectionService.GetScenarioAsync(id);
        if (scenario is null) return NotFound();

        var accounts = await _projectionService.GetActiveAccountsAsync();
        var vm = BuildFormViewModel(scenario, accounts);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ScenarioFormViewModel vm)
    {
        if (!ModelState.IsValid)
            return View(vm);

        var scenario = await _projectionService.GetScenarioAsync(id);
        if (scenario is null) return NotFound();

        scenario.Name = vm.Name;
        scenario.BirthYear = vm.BirthYear;
        scenario.RetirementAge = vm.RetirementAge;
        scenario.LifeExpectancy = vm.LifeExpectancy;
        scenario.AnnualRetirementSpending = vm.AnnualRetirementSpending;
        scenario.InflationRate = vm.InflationRate;
        scenario.ReturnStdDev = vm.ReturnStdDev;

        scenario.AccountAssumptions.Clear();
        foreach (var a in vm.Accounts)
        {
            scenario.AccountAssumptions.Add(new ProjectionAccountAssumption
            {
                ScenarioId = scenario.Id,
                AccountId = a.AccountId,
                AnnualContribution = a.AnnualContribution,
                EmployerMatch = a.EmployerMatch,
                ExpectedReturnRate = a.ExpectedReturnRate,
                ContributionEndAge = a.ContributionEndAge
            });
        }

        scenario.Events.Clear();
        foreach (var e in vm.Events.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
        {
            scenario.Events.Add(new ProjectionEvent
            {
                ScenarioId = scenario.Id,
                Name = e.Name,
                Age = e.Age,
                Amount = e.Amount,
                IsRecurring = e.IsRecurring,
                EndAge = e.EndAge
            });
        }

        await _projectionService.SaveScenarioAsync(scenario);
        return RedirectToAction(nameof(Results), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await _projectionService.DeleteScenarioAsync(id);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Results(int id)
    {
        var scenario = await _projectionService.GetScenarioAsync(id);
        if (scenario is null) return NotFound();

        var input = BuildProjectionInput(scenario);
        var result = ProjectionService.RunProjection(input);
        var monteCarlo = ProjectionService.RunMonteCarlo(input, scenario.ReturnStdDev);
        var fire = ProjectionService.ComputeFire(input);

        var vm = new ScenarioResultsViewModel
        {
            ScenarioId = scenario.Id,
            ScenarioName = scenario.Name,
            RetirementAge = scenario.RetirementAge,
            LifeExpectancy = scenario.LifeExpectancy,
            AnnualRetirementSpending = scenario.AnnualRetirementSpending,
            Result = result,
            MonteCarlo = monteCarlo,
            Fire = fire,
            Accounts = scenario.AccountAssumptions.Select(a => new AccountInfo
            {
                Id = a.AccountId,
                Name = a.Account.Name,
                Type = a.Account.Type
            }).ToList(),
            LifeEvents = input.LifeEvents
        };

        return View(vm);
    }

    public async Task<IActionResult> Compare()
    {
        var scenarios = await _projectionService.GetScenariosAsync();
        if (scenarios.Count < 2)
            return RedirectToAction(nameof(Index));

        var items = new List<CompareItem>();
        foreach (var scenario in scenarios)
        {
            var fullScenario = await _projectionService.GetScenarioAsync(scenario.Id);
            if (fullScenario is null) continue;

            var input = BuildProjectionInput(fullScenario);
            var result = ProjectionService.RunProjection(input);
            var monteCarlo = ProjectionService.RunMonteCarlo(input, fullScenario.ReturnStdDev);
            var fire = ProjectionService.ComputeFire(input);

            items.Add(new CompareItem
            {
                ScenarioId = scenario.Id,
                ScenarioName = scenario.Name,
                RetirementAge = scenario.RetirementAge,
                RetirementBalance = result.RetirementBalance,
                DepletionAge = result.DepletionAge,
                SurplusAtDeath = result.SurplusAtDeath,
                SafeSpendingEstimate = result.SafeSpendingEstimate,
                SuccessProbability = monteCarlo.SuccessProbability,
                FireNumber = fire.FireNumber,
                ProjectedFireAge = fire.ProjectedFireAge
            });
        }

        return View(new ScenarioCompareViewModel { Items = items });
    }

    private static ScenarioFormViewModel BuildFormViewModel(ProjectionScenario? scenario, List<Account> accounts)
    {
        var vm = new ScenarioFormViewModel();

        if (scenario is not null)
        {
            vm.Id = scenario.Id;
            vm.Name = scenario.Name;
            vm.BirthYear = scenario.BirthYear;
            vm.RetirementAge = scenario.RetirementAge;
            vm.LifeExpectancy = scenario.LifeExpectancy;
            vm.AnnualRetirementSpending = scenario.AnnualRetirementSpending;
            vm.InflationRate = scenario.InflationRate;
            vm.ReturnStdDev = scenario.ReturnStdDev;
            vm.Events = scenario.Events.Select(e => new LifeEventFormItem
            {
                Name = e.Name,
                Age = e.Age,
                Amount = e.Amount,
                IsRecurring = e.IsRecurring,
                EndAge = e.EndAge
            }).ToList();
        }

        vm.Accounts = accounts.Select(a =>
        {
            var existing = scenario?.AccountAssumptions.FirstOrDefault(aa => aa.AccountId == a.Id);
            var latestBalance = a.Snapshots.MaxBy(s => s.Date)?.Balance ?? 0;

            return new AccountAssumptionFormItem
            {
                AccountId = a.Id,
                AccountName = a.Name,
                AccountType = a.Type,
                CurrentBalance = latestBalance,
                AnnualContribution = existing?.AnnualContribution ?? GetDefaultContribution(a.Type),
                EmployerMatch = existing?.EmployerMatch ?? 0,
                ExpectedReturnRate = existing?.ExpectedReturnRate ?? GetDefaultReturnRate(a.Type),
                ContributionEndAge = existing?.ContributionEndAge ?? 65
            };
        }).ToList();

        return vm;
    }

    private static decimal GetDefaultContribution(AccountType type) => type switch
    {
        AccountType.Retirement401k => 20_500,
        AccountType.RothIra or AccountType.TraditionalIra => 6_500,
        _ => 0
    };

    private static decimal GetDefaultReturnRate(AccountType type) => type switch
    {
        AccountType.Checking => 0.001m,
        AccountType.Savings => 0.04m,
        AccountType.Retirement401k or AccountType.RothIra or AccountType.TraditionalIra or AccountType.Brokerage => 0.07m,
        _ => 0.05m
    };

    private static ProjectionInput BuildProjectionInput(ProjectionScenario scenario)
    {
        return new ProjectionInput
        {
            CurrentYear = DateTime.Today.Year,
            BirthYear = scenario.BirthYear,
            RetirementAge = scenario.RetirementAge,
            LifeExpectancy = scenario.LifeExpectancy,
            AnnualRetirementSpending = scenario.AnnualRetirementSpending,
            InflationRate = scenario.InflationRate,
            AccountAssumptions = scenario.AccountAssumptions.Select(a => new AccountAssumptionInput
            {
                AccountId = a.AccountId,
                AccountName = a.Account.Name,
                AccountType = a.Account.Type,
                CurrentBalance = a.Account.Snapshots.MaxBy(s => s.Date)?.Balance ?? 0,
                AnnualContribution = a.AnnualContribution,
                EmployerMatch = a.EmployerMatch,
                ExpectedReturnRate = a.ExpectedReturnRate,
                ContributionEndAge = a.ContributionEndAge
            }).ToList(),
            LifeEvents = scenario.Events.Select(e => new LifeEventInput
            {
                Name = e.Name,
                Age = e.Age,
                Amount = e.Amount,
                IsRecurring = e.IsRecurring,
                EndAge = e.EndAge
            }).ToList()
        };
    }
}
