using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using LoanProposal.API.Models;
using LoanProposal.API.Services;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Enums;
using LoanProposal.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using static LoanProposal.API.Controllers.MvcCrudHelpers;

namespace LoanProposal.API.Controllers;

[Authorize(Policy = "LoanParticipant")]
public class DashboardController : Controller
{
    private readonly ITenantRegistryClient _tenantRegistryClient;
    private readonly TenantDbContextFactory _tenantDbFactory;

    public DashboardController(ITenantRegistryClient tenantRegistryClient, TenantDbContextFactory tenantDbFactory)
    {
        _tenantRegistryClient = tenantRegistryClient;
        _tenantDbFactory = tenantDbFactory;
    }

    [HttpGet("/")]
    [HttpGet("/dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new DashboardViewModel { Tenants = await TenantSummariesAsync(_tenantRegistryClient, _tenantDbFactory, User, ct) });

    internal static async Task<IReadOnlyList<TenantSummaryViewModel>> TenantSummariesAsync(ITenantRegistryClient tenantRegistryClient, TenantDbContextFactory tenantDbFactory, ClaimsPrincipal user, CancellationToken ct)
    {
        var tenants = await tenantRegistryClient.GetTenantsAsync(ct);
        if (!user.IsInRole(RoleNames.PlatformAdmin) && MvcCrudHelpers.CurrentTenantId(user) is Guid tenantId)
            tenants = tenants.Where(t => t.Id == tenantId).ToList();

        var summaries = new List<TenantSummaryViewModel>();

        foreach (var tenantOption in tenants.OrderBy(t => t.Name))
        {
            var tenant = await tenantRegistryClient.GetLoanProposalTenantAsync(tenantOption.Id, ct);
            if (tenant is null) continue;

            var appCount = 0;
            var customFieldCount = 0;
            var ruleCount = 0;
            var workflowCount = 0;
            try
            {
                await using var tenantDb = tenantDbFactory.CreateDbContext(tenant.ConnectionString);
                appCount = await tenantDb.LoanApplications.CountAsync(ct);
                customFieldCount = await tenantDb.CustomFields.CountAsync(ct);
                ruleCount = await tenantDb.RuleDefinitions.CountAsync(ct);
                workflowCount = await tenantDb.WorkflowDefinitions.CountAsync(ct);
            }
            catch { }

            summaries.Add(new TenantSummaryViewModel
            {
                Id = tenant.TenantId,
                Name = tenant.TenantName,
                Slug = tenant.TenantSlug,
                Currency = tenant.Currency,
                Timezone = tenant.Timezone,
                IsActive = tenant.IsActive,
                ApplicationCount = appCount,
                CustomFieldCount = customFieldCount,
                RuleCount = ruleCount,
                WorkflowCount = workflowCount
            });
        }

        return summaries;
    }
}

[Authorize(Policy = "PlatformAdmin")]
[Route("platform-tenants")]
public class TenantsController : Controller
{
    private readonly IConfiguration _configuration;

    public TenantsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("")]
    public IActionResult Index() => Redirect($"{_configuration["TenantRegistry:BaseUrl"] ?? "http://localhost:5101"}/platform/tenants");

    [HttpGet("{id:guid}/edit")]
    public IActionResult Edit(Guid id) => Redirect($"{_configuration["TenantRegistry:BaseUrl"] ?? "http://localhost:5101"}/platform/tenants");

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public IActionResult Create(CreateTenantForm form) => RedirectToAction(nameof(Index));

    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(Guid id, TenantEditForm form) => RedirectToAction(nameof(Index));

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(Guid id) => RedirectToAction(nameof(Index));
}

[Authorize(Policy = "TenantAdmin")]
[Route("tenant-configuration")]
public class ConfigurationController : Controller
{
    private readonly ITenantRegistryClient _tenantRegistryClient;
    private readonly TenantDbContextFactory _tenantDbFactory;

    public ConfigurationController(ITenantRegistryClient tenantRegistryClient, TenantDbContextFactory tenantDbFactory)
    {
        _tenantRegistryClient = tenantRegistryClient;
        _tenantDbFactory = tenantDbFactory;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? tenantId, CancellationToken ct) =>
        View(await BuildConfigurationModelAsync(tenantId, ct));

    [HttpPost("settings/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSetting(TenantConfigurationForm form, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(form.TenantId, ct);
        if (tenantDb is null) return NotFound();

        if (form.Id.HasValue)
        {
            var setting = await tenantDb.TenantConfigurations.FirstOrDefaultAsync(c => c.Id == form.Id, ct);
            if (setting is null) return NotFound();
            Set(setting, nameof(TenantConfiguration.Key), form.Key.Trim());
            setting.Update(form.Value.Trim(), "mvc");
            Set(setting, nameof(TenantConfiguration.ValueType), form.ValueType);
            Set(setting, nameof(TenantConfiguration.Description), form.Description);
        }
        else
        {
            tenantDb.TenantConfigurations.Add(TenantConfiguration.Create(form.TenantId, form.Key.Trim(), form.Value.Trim(), form.ValueType, "mvc", form.Description));
        }

        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Tenant setting saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("settings/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSetting(Guid id, Guid tenantId, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var setting = await tenantDb.TenantConfigurations.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (setting is null) return NotFound();
        tenantDb.TenantConfigurations.Remove(setting);
        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Tenant setting deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }
    [HttpPost("custom-fields/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCustomField(CustomFieldForm form, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(form.TenantId, ct);
        if (tenantDb is null) return NotFound();

        if (form.Id.HasValue)
        {
            var field = await tenantDb.CustomFields.FirstOrDefaultAsync(f => f.Id == form.Id, ct);
            if (field is null) return NotFound();
            Set(field, nameof(CustomField.FieldKey), form.FieldKey.Trim());
            Set(field, nameof(CustomField.Label), form.Label.Trim());
            Set(field, nameof(CustomField.FieldType), form.FieldType);
            Set(field, nameof(CustomField.IsRequired), form.IsRequired);
            Set(field, nameof(CustomField.IsSearchable), form.IsSearchable);
            Set(field, nameof(CustomField.IsActive), form.IsActive);
        }
        else
        {
            tenantDb.CustomFields.Add(CustomField.Create(form.TenantId, form.FieldKey.Trim(), form.Label.Trim(), form.FieldType, form.IsRequired, form.IsSearchable));
        }

        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Custom field saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("custom-fields/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCustomField(Guid id, Guid tenantId, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var field = await tenantDb.CustomFields.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (field is null) return NotFound();
        tenantDb.CustomFields.Remove(field);
        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Custom field deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    [HttpPost("rules/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRule(RuleForm form, CancellationToken ct)
    {
        try { ValidateJson(form.Expression, "Rule expression"); }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        await using var tenantDb = await OpenTenantDbAsync(form.TenantId, ct);
        if (tenantDb is null) return NotFound();

        if (form.Id.HasValue)
        {
            var rule = await tenantDb.RuleDefinitions.FirstOrDefaultAsync(r => r.Id == form.Id, ct);
            if (rule is null) return NotFound();
            Set(rule, nameof(RuleDefinition.Name), form.Name.Trim());
            Set(rule, nameof(RuleDefinition.Category), form.Category);
            Set(rule, nameof(RuleDefinition.Expression), form.Expression.Trim());
            Set(rule, nameof(RuleDefinition.OutcomeWhenTrue), form.Outcome);
            Set(rule, nameof(RuleDefinition.Priority), form.Priority);
            Set(rule, nameof(RuleDefinition.IsActive), form.IsActive);
        }
        else
        {
            var rule = RuleDefinition.Create(form.TenantId, form.Name.Trim(), form.Category, form.Expression.Trim(), form.Outcome, "mvc", form.Priority);
            Set(rule, nameof(RuleDefinition.IsActive), form.IsActive);
            tenantDb.RuleDefinitions.Add(rule);
        }

        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Rule saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("rules/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRule(Guid id, Guid tenantId, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var rule = await tenantDb.RuleDefinitions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();
        tenantDb.RuleDefinitions.Remove(rule);
        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Rule deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    [HttpGet("workflows/{id:guid}")]
    public async Task<IActionResult> WorkflowDetails(Guid id, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_tenantRegistryClient, User, ct);
        foreach (var tenantOption in tenants)
        {
            await using var tenantDb = await OpenTenantDbAsync(tenantOption.Id, ct);
            if (tenantDb is null) continue;
            var workflow = await tenantDb.WorkflowDefinitions.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (workflow is null) continue;

            return View(new WorkflowDetailsViewModel
            {
                Id = workflow.Id,
                TenantId = workflow.TenantId,
                TenantName = tenantOption.Name,
                Name = workflow.Name,
                Version = workflow.Version,
                IsActive = workflow.IsActive,
                EffectiveFrom = workflow.EffectiveFrom,
                CreatedAt = workflow.CreatedAt,
                CreatedBy = workflow.CreatedBy,
                Steps = workflow.GetSteps(),
                RoutingRules = workflow.GetRoutingRules(),
                StepsJson = PrettyJson(workflow.StepsJson),
                RoutingRulesJson = PrettyJson(workflow.RoutingRulesJson)
            });
        }

        return NotFound();
    }

    [HttpPost("workflows/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveWorkflow(WorkflowForm form, CancellationToken ct)
    {
        List<WorkflowStepDefinition>? steps;
        List<RoutingRule>? routes;
        try
        {
            steps = Deserialize<List<WorkflowStepDefinition>>(form.StepsJson, "Workflow steps");
            routes = Deserialize<List<RoutingRule>>(form.RoutingRulesJson, "Workflow routing rules");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        await using var tenantDb = await OpenTenantDbAsync(form.TenantId, ct);
        if (tenantDb is null) return NotFound();

        if (form.Id.HasValue)
        {
            var workflow = await tenantDb.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == form.Id, ct);
            if (workflow is null) return NotFound();
            Set(workflow, nameof(WorkflowDefinition.Name), form.Name.Trim());
            Set(workflow, nameof(WorkflowDefinition.EffectiveFrom), form.EffectiveFrom);
            Set(workflow, nameof(WorkflowDefinition.IsActive), form.IsActive);
            workflow.SetSteps(steps!);
            workflow.SetRoutingRules(routes!);
        }
        else
        {
            var workflow = WorkflowDefinition.Create(form.TenantId, form.Name.Trim(), "mvc", form.EffectiveFrom);
            workflow.SetSteps(steps!);
            workflow.SetRoutingRules(routes!);
            if (form.IsActive) workflow.Activate(form.EffectiveFrom);
            tenantDb.WorkflowDefinitions.Add(workflow);
        }

        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Workflow saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("workflows/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteWorkflow(Guid id, Guid tenantId, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var workflow = await tenantDb.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        tenantDb.WorkflowDefinitions.Remove(workflow);
        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Workflow deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }
    private async Task<TenantConfigurationViewModel> BuildConfigurationModelAsync(Guid? tenantId, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_tenantRegistryClient, User, ct);
        tenantId ??= tenants.FirstOrDefault()?.Id;

        var configuration = new List<ConfigurationItemViewModel>();
        var customFields = new List<CustomFieldViewModel>();
        var rules = new List<RuleItemViewModel>();
        var workflows = new List<WorkflowItemViewModel>();

        if (tenantId.HasValue)
        {
            await using var tenantDb = await OpenTenantDbAsync(tenantId.Value, ct);
            if (tenantDb is not null)
            {
                configuration = await tenantDb.TenantConfigurations.AsNoTracking().OrderBy(c => c.Key)
                    .Select(c => new ConfigurationItemViewModel { Id = c.Id, TenantId = c.TenantId, Key = c.Key, Value = c.Value, ValueType = c.ValueType, Description = c.Description }).ToListAsync(ct);
                customFields = await tenantDb.CustomFields.AsNoTracking().OrderBy(f => f.DisplayOrder).ThenBy(f => f.Label)
                    .Select(f => new CustomFieldViewModel { Id = f.Id, TenantId = f.TenantId, FieldKey = f.FieldKey, Label = f.Label, FieldType = f.FieldType, IsRequired = f.IsRequired, IsSearchable = f.IsSearchable, IsActive = f.IsActive }).ToListAsync(ct);
                rules = await tenantDb.RuleDefinitions.AsNoTracking().OrderBy(r => r.Category).ThenBy(r => r.Priority)
                    .Select(r => new RuleItemViewModel { Id = r.Id, TenantId = r.TenantId, Name = r.Name, Category = r.Category, Outcome = r.OutcomeWhenTrue, Priority = r.Priority, IsActive = r.IsActive, Expression = r.Expression }).ToListAsync(ct);
                var workflowEntities = await tenantDb.WorkflowDefinitions.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
                workflows = workflowEntities.Select(w => new WorkflowItemViewModel
                {
                    Id = w.Id,
                    TenantId = w.TenantId,
                    Name = w.Name,
                    Version = w.Version,
                    IsActive = w.IsActive,
                    EffectiveFrom = w.EffectiveFrom,
                    StepCount = w.GetSteps().Count,
                    RoutingRuleCount = w.GetRoutingRules().Count,
                    StepsJson = w.StepsJson,
                    RoutingRulesJson = w.RoutingRulesJson
                }).ToList();
            }
        }

        return new TenantConfigurationViewModel
        {
            SelectedTenantId = tenantId,
            Tenants = tenants,
            Configuration = configuration,
            CustomFields = customFields,
            Rules = rules,
            Workflows = workflows,
            ConfigurationForm = new TenantConfigurationForm { TenantId = tenantId ?? Guid.Empty },
            CustomFieldForm = new CustomFieldForm { TenantId = tenantId ?? Guid.Empty },
            RuleForm = new RuleForm { TenantId = tenantId ?? Guid.Empty },
            WorkflowForm = new WorkflowForm { TenantId = tenantId ?? Guid.Empty }
        };
    }

    private async Task<AppDbContext?> OpenTenantDbAsync(Guid tenantId, CancellationToken ct)
    {
        if (!MvcCrudHelpers.CanAccessTenant(User, tenantId)) return null;
        var tenant = await _tenantRegistryClient.GetLoanProposalTenantAsync(tenantId, ct);
        if (tenant is null) return null;

        var tenantDb = _tenantDbFactory.CreateDbContext(tenant.ConnectionString);
        await tenantDb.Database.EnsureCreatedAsync(ct);
        return tenantDb;
    }
}

[Authorize(Policy = "LoanParticipant")]
[Route("loan-applications")]
public class ApplicationsController : Controller
{
    private readonly ITenantRegistryClient _tenantRegistryClient;
    private readonly TenantDbContextFactory _tenantDbFactory;

    public ApplicationsController(ITenantRegistryClient tenantRegistryClient, TenantDbContextFactory tenantDbFactory)
    {
        _tenantRegistryClient = tenantRegistryClient;
        _tenantDbFactory = tenantDbFactory;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? tenantId, LoanApplicationStatus? status, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_tenantRegistryClient, User, ct);
        tenantId ??= tenants.FirstOrDefault()?.Id;
        var tenantName = tenants.FirstOrDefault(t => t.Id == tenantId)?.Name ?? "Unknown tenant";
        var apps = new List<ApplicationListItemViewModel>();
        var fields = new List<CustomFieldViewModel>();
        var workflows = new List<WorkflowItemViewModel>();

        if (tenantId.HasValue)
        {
            await using var tenantDb = await OpenTenantDbAsync(tenantId.Value, ct);
            if (tenantDb is not null)
            {
                var appEntities = await tenantDb.LoanApplications.AsNoTracking()
                    .Where(a => status == null || a.Status == status)
                    .OrderByDescending(a => a.SubmittedAt)
                    .ToListAsync(ct);
                apps = appEntities.Select(a => new ApplicationListItemViewModel
                {
                    Id = a.Id,
                    ApplicationNumber = a.ApplicationNumber,
                    TenantName = tenantName,
                    RequestedAmount = a.RequestedAmount,
                    RequestedTenureMonths = a.RequestedTenureMonths,
                    Status = a.Status,
                    CurrentStepId = a.CurrentStepId,
                    SubmittedAt = a.SubmittedAt
                }).ToList();

                fields = await tenantDb.CustomFields.AsNoTracking().Where(f => f.IsSearchable)
                    .Select(f => new CustomFieldViewModel { Id = f.Id, TenantId = f.TenantId, FieldKey = f.FieldKey, Label = f.Label, FieldType = f.FieldType, IsRequired = f.IsRequired, IsSearchable = f.IsSearchable, IsActive = f.IsActive }).ToListAsync(ct);
                var workflowEntities = await tenantDb.WorkflowDefinitions.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
                workflows = workflowEntities.Select(w => new WorkflowItemViewModel
                {
                    Id = w.Id,
                    TenantId = w.TenantId,
                    Name = w.Name,
                    Version = w.Version,
                    IsActive = w.IsActive,
                    EffectiveFrom = w.EffectiveFrom,
                    StepCount = w.GetSteps().Count,
                    RoutingRuleCount = w.GetRoutingRules().Count
                }).ToList();
            }
        }

        return View(new ApplicationsIndexViewModel
        {
            SelectedTenantId = tenantId,
            Status = status,
            Tenants = tenants,
            Applications = apps,
            SearchableFields = fields,
            Workflows = workflows,
            Form = new LoanProposalForm { TenantId = tenantId ?? Guid.Empty }
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, Guid tenantId, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_tenantRegistryClient, User, ct);
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var app = await tenantDb.LoanApplications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        return View(new ApplicationDetailsViewModel
        {
            Id = app.Id,
            ApplicationNumber = app.ApplicationNumber,
            TenantName = tenants.FirstOrDefault(t => t.Id == tenantId)?.Name ?? "Unknown tenant",
            RequestedAmount = app.RequestedAmount,
            RequestedTenureMonths = app.RequestedTenureMonths,
            Status = app.Status,
            CurrentStepId = app.CurrentStepId,
            SubmittedAt = app.SubmittedAt,
            CustomDataJson = app.CustomDataJson
        });
    }

    [HttpPost("save")]
    [Authorize(Policy = "LoanOfficer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(LoanProposalForm form, CancellationToken ct)
    {
        try { ValidateJson(form.CustomDataJson, "Custom data"); }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        await using var tenantDb = await OpenTenantDbAsync(form.TenantId, ct);
        if (tenantDb is null) return NotFound();
        var workflow = await tenantDb.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == form.WorkflowDefinitionId, ct);
        if (workflow is null)
        {
            TempData["Error"] = "Select a workflow before creating a loan proposal.";
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        var applicant = await tenantDb.Applicants.FirstOrDefaultAsync(a => a.NationalId == form.NationalId, ct)
            ?? Applicant.Create(form.TenantId, form.ApplicantName, form.ApplicantEmail, form.ApplicantPhone, form.NationalId, form.DateOfBirth);
        applicant.UpdateFinancials(form.AnnualIncome, form.CreditScore, form.DebtToIncomeRatio, form.RelationshipYears);
        if (tenantDb.Entry(applicant).State == EntityState.Detached) tenantDb.Applicants.Add(applicant);
        var product = await EnsureDefaultProductAsync(tenantDb, form.TenantId, workflow.Id, ct);

        if (form.Id.HasValue)
        {
            var existing = await tenantDb.LoanApplications.FirstOrDefaultAsync(a => a.Id == form.Id, ct);
            if (existing is null) return NotFound();
            Set(existing, nameof(LoanApplication.ApplicantId), applicant.Id);
            Set(existing, nameof(LoanApplication.LoanProductId), product.Id);
            Set(existing, nameof(LoanApplication.WorkflowDefinitionId), workflow.Id);
            Set(existing, nameof(LoanApplication.RequestedAmount), form.RequestedAmount);
            Set(existing, nameof(LoanApplication.RequestedTenureMonths), form.RequestedTenureMonths);
            Set(existing, nameof(LoanApplication.CustomDataJson), form.CustomDataJson.Trim());
        }
        else
        {
            var proposal = LoanApplication.Create(form.TenantId, product.Id, applicant.Id, workflow.Id, form.RequestedAmount, form.RequestedTenureMonths);
            Set(proposal, nameof(LoanApplication.CurrentStepId), workflow.GetSteps().FirstOrDefault()?.StepId ?? "draft");
            foreach (var item in JsonSerializer.Deserialize<Dictionary<string, object?>>(form.CustomDataJson) ?? []) proposal.SetCustomField(item.Key, item.Value);
            proposal.Submit();
            tenantDb.LoanApplications.Add(proposal);
        }

        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Loan proposal saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }
    [HttpPost("{id:guid}/delete")]
    [Authorize(Policy = "TenantAdmin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, Guid tenantId, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var proposal = await tenantDb.LoanApplications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (proposal is null) return NotFound();
        tenantDb.LoanApplications.Remove(proposal);
        await tenantDb.SaveChangesAsync(ct);
        TempData["Success"] = "Loan proposal deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    [HttpPost("{id:guid}/advance")]
    [Authorize(Policy = "LoanParticipant")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Advance(Guid id, Guid tenantId, string action, CancellationToken ct)
    {
        await using var tenantDb = await OpenTenantDbAsync(tenantId, ct);
        if (tenantDb is null) return NotFound();
        var proposal = await tenantDb.LoanApplications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (proposal is null) return NotFound();

        switch (action)
        {
            case "send_review":
                proposal.AdvanceTo("branch_review", "mvc", "Sent to branch review");
                TempData["Success"] = "Proposal moved to Branch Review.";
                break;
            case "approve":
                if (!User.IsInRole(RoleNames.Approver) && !User.IsInRole(RoleNames.TenantAdmin) && !User.IsInRole(RoleNames.PlatformAdmin)) return Forbid();
                proposal.AdvanceTo("approved", "mvc", "Approved");
                proposal.Approve("mvc", "Approved from MVC");
                TempData["Success"] = "Proposal approved.";
                break;
            case "reject":
                if (!User.IsInRole(RoleNames.Approver) && !User.IsInRole(RoleNames.TenantAdmin) && !User.IsInRole(RoleNames.PlatformAdmin)) return Forbid();
                proposal.AdvanceTo("rejected", "mvc", "Rejected");
                proposal.Reject("mvc", "Rejected from MVC");
                TempData["Success"] = "Proposal rejected.";
                break;
            default:
                TempData["Error"] = "Unknown workflow action.";
                break;
        }

        await tenantDb.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    private async Task<LoanProduct> EnsureDefaultProductAsync(AppDbContext tenantDb, Guid tenantId, Guid workflowId, CancellationToken ct)
    {
        var product = await tenantDb.LoanProducts.FirstOrDefaultAsync(p => p.WorkflowDefinitionId == workflowId && p.Code == "DEFAULT", ct);
        if (product is not null) return product;

        product = LoanProduct.Create(tenantId, "Default Loan Product", "DEFAULT", LoanProductType.Personal, 1, 999999999, workflowId);
        tenantDb.LoanProducts.Add(product);
        return product;
    }

    private async Task<AppDbContext?> OpenTenantDbAsync(Guid tenantId, CancellationToken ct)
    {
        if (!MvcCrudHelpers.CanAccessTenant(User, tenantId)) return null;
        var tenant = await _tenantRegistryClient.GetLoanProposalTenantAsync(tenantId, ct);
        if (tenant is null) return null;

        var tenantDb = _tenantDbFactory.CreateDbContext(tenant.ConnectionString);
        await tenantDb.Database.EnsureCreatedAsync(ct);
        return tenantDb;
    }
}

internal static class MvcCrudHelpers
{
    public static Guid? CurrentTenantId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AuthClaimTypes.TenantId), out var tenantId) ? tenantId : null;

    public static bool CanAccessTenant(ClaimsPrincipal user, Guid tenantId) =>
        user.IsInRole(RoleNames.PlatformAdmin) || CurrentTenantId(user) == tenantId;

    public static async Task<IReadOnlyList<TenantOption>> GetTenantOptionsAsync(ITenantRegistryClient tenantRegistryClient, ClaimsPrincipal user, CancellationToken ct)
    {
        var tenants = await tenantRegistryClient.GetTenantsAsync(ct);
        if (!user.IsInRole(RoleNames.PlatformAdmin) && CurrentTenantId(user) is Guid tenantId)
            tenants = tenants.Where(t => t.Id == tenantId).ToList();

        return tenants
            .OrderBy(t => t.Name)
            .Select(t => new TenantOption(t.Id, t.Name, t.Slug, t.Currency))
            .ToList();
    }

    public static void Set<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().Name}.");
        property.SetValue(target, value);
    }

    public static T? Deserialize<T>(string json, string label)
    {
        try { return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException ex) { throw new InvalidOperationException($"{label} contains invalid JSON: {ex.Message}", ex); }
    }

    public static string PrettyJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    public static void ValidateJson(string json, string label)
    {
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidOperationException($"{label} contains invalid JSON: {ex.Message}", ex); }
    }
}
