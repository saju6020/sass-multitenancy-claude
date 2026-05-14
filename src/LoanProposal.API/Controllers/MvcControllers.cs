using System.Reflection;
using System.Text.Json;
using LoanProposal.API.Models;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Enums;
using LoanProposal.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LoanProposal.API.Controllers.MvcCrudHelpers;

namespace LoanProposal.API.Controllers;

[AllowAnonymous]
public class DashboardController : Controller
{
    private readonly AppDbContext _db;

    public DashboardController(AppDbContext db) => _db = db;

    [HttpGet("/")]
    [HttpGet("/dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new DashboardViewModel { Tenants = await TenantSummariesAsync(_db, ct) });

    internal static async Task<IReadOnlyList<TenantSummaryViewModel>> TenantSummariesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var applications = await CountByTenantAsync(db.LoanApplications.IgnoreQueryFilters(), a => a.TenantId, ct);
        var customFields = await CountByTenantAsync(db.CustomFields.IgnoreQueryFilters(), f => f.TenantId, ct);
        var rules = await CountByTenantAsync(db.RuleDefinitions.IgnoreQueryFilters(), r => r.TenantId, ct);
        var workflows = await CountByTenantAsync(db.WorkflowDefinitions.IgnoreQueryFilters(), w => w.TenantId, ct);

        return tenants.Select(t => new TenantSummaryViewModel
        {
            Id = t.Id,
            Name = t.Name,
            Slug = t.Slug,
            Currency = t.DefaultCurrency,
            Timezone = t.DefaultTimezone,
            IsActive = t.IsActive,
            ApplicationCount = applications.GetValueOrDefault(t.Id),
            CustomFieldCount = customFields.GetValueOrDefault(t.Id),
            RuleCount = rules.GetValueOrDefault(t.Id),
            WorkflowCount = workflows.GetValueOrDefault(t.Id)
        }).ToList();
    }

    private static async Task<Dictionary<Guid, int>> CountByTenantAsync<TEntity>(
        IQueryable<TEntity> query,
        System.Linq.Expressions.Expression<Func<TEntity, Guid>> tenantSelector,
        CancellationToken ct)
        where TEntity : class =>
        await query.AsNoTracking()
            .GroupBy(tenantSelector)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, ct);
}

[AllowAnonymous]
[Route("platform-tenants")]
public class TenantsController : Controller
{
    private readonly AppDbContext _db;

    public TenantsController(AppDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new TenantListViewModel { Tenants = await DashboardController.TenantSummariesAsync(_db, ct) });

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        return View(new TenantEditForm
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Slug = tenant.Slug,
            Currency = tenant.DefaultCurrency,
            Timezone = tenant.DefaultTimezone,
            IsActive = tenant.IsActive
        });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateTenantForm form, CancellationToken ct)
    {
        if (!ValidateTenantForm(form)) return RedirectToAction(nameof(Index));

        var slug = form.Slug.Trim().ToLowerInvariant();
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            TempData["Error"] = $"Slug '{slug}' is already taken.";
            return RedirectToAction(nameof(Index));
        }

        _db.Tenants.Add(Tenant.Create(form.Name.Trim(), slug, form.Currency.Trim(), form.Timezone.Trim()));
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Tenant created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, TenantEditForm form, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();
        if (!ValidateTenantForm(form)) return View(form);

        Set(tenant, nameof(Tenant.Name), form.Name.Trim());
        Set(tenant, nameof(Tenant.Slug), form.Slug.Trim().ToLowerInvariant());
        Set(tenant, nameof(Tenant.DefaultCurrency), form.Currency.Trim().ToUpperInvariant());
        Set(tenant, nameof(Tenant.DefaultTimezone), form.Timezone.Trim());
        Set(tenant, nameof(Tenant.IsActive), form.IsActive);
        await _db.SaveChangesAsync(ct);

        TempData["Success"] = "Tenant updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Tenant deleted.";
        return RedirectToAction(nameof(Index));
    }

    private bool ValidateTenantForm(CreateTenantForm form)
    {
        if (!string.IsNullOrWhiteSpace(form.Name) && !string.IsNullOrWhiteSpace(form.Slug)) return true;
        TempData["Error"] = "Tenant name and slug are required.";
        return false;
    }
}

[AllowAnonymous]
[Route("tenant-configuration")]
public class ConfigurationController : Controller
{
    private readonly AppDbContext _db;

    public ConfigurationController(AppDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? tenantId, CancellationToken ct)
    {
        var model = await BuildConfigurationModelAsync(tenantId, ct);
        return View(model);
    }

    [HttpPost("settings/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSetting(TenantConfigurationForm form, CancellationToken ct)
    {
        if (form.Id.HasValue)
        {
            var setting = await _db.TenantConfigurations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == form.Id, ct);
            if (setting is null) return NotFound();
            Set(setting, nameof(TenantConfiguration.Key), form.Key.Trim());
            setting.Update(form.Value.Trim(), "mvc");
            Set(setting, nameof(TenantConfiguration.ValueType), form.ValueType);
            Set(setting, nameof(TenantConfiguration.Description), form.Description);
        }
        else
        {
            _db.TenantConfigurations.Add(TenantConfiguration.Create(
                form.TenantId, form.Key.Trim(), form.Value.Trim(), form.ValueType, "mvc", form.Description));
        }

        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Tenant setting saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("settings/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSetting(Guid id, Guid tenantId, CancellationToken ct)
    {
        var setting = await _db.TenantConfigurations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (setting is null) return NotFound();
        _db.TenantConfigurations.Remove(setting);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Tenant setting deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    [HttpPost("custom-fields/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCustomField(CustomFieldForm form, CancellationToken ct)
    {
        if (form.Id.HasValue)
        {
            var field = await _db.CustomFields.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == form.Id, ct);
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
            _db.CustomFields.Add(CustomField.Create(
                form.TenantId, form.FieldKey.Trim(), form.Label.Trim(), form.FieldType, form.IsRequired, form.IsSearchable));
        }

        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Custom field saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("custom-fields/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCustomField(Guid id, Guid tenantId, CancellationToken ct)
    {
        var field = await _db.CustomFields.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (field is null) return NotFound();
        _db.CustomFields.Remove(field);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Custom field deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    [HttpPost("rules/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRule(RuleForm form, CancellationToken ct)
    {
        try
        {
            ValidateJson(form.Expression, "Rule expression");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        if (form.Id.HasValue)
        {
            var rule = await _db.RuleDefinitions.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == form.Id, ct);
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
            var rule = RuleDefinition.Create(
                form.TenantId, form.Name.Trim(), form.Category, form.Expression.Trim(), form.Outcome, "mvc", form.Priority);
            Set(rule, nameof(RuleDefinition.IsActive), form.IsActive);
            _db.RuleDefinitions.Add(rule);
        }

        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Rule saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("rules/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRule(Guid id, Guid tenantId, CancellationToken ct)
    {
        var rule = await _db.RuleDefinitions.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();
        _db.RuleDefinitions.Remove(rule);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Rule deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
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

        if (form.Id.HasValue)
        {
            var workflow = await _db.WorkflowDefinitions.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == form.Id, ct);
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
            _db.WorkflowDefinitions.Add(workflow);
        }

        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Workflow saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("workflows/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteWorkflow(Guid id, Guid tenantId, CancellationToken ct)
    {
        var workflow = await _db.WorkflowDefinitions.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        _db.WorkflowDefinitions.Remove(workflow);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Workflow deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    private async Task<TenantConfigurationViewModel> BuildConfigurationModelAsync(Guid? tenantId, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_db, ct);
        tenantId ??= tenants.FirstOrDefault()?.Id;

        var configuration = await _db.TenantConfigurations.IgnoreQueryFilters().AsNoTracking()
            .Where(c => tenantId == null || c.TenantId == tenantId)
            .OrderBy(c => c.Key)
            .Select(c => new ConfigurationItemViewModel
            {
                Id = c.Id,
                TenantId = c.TenantId,
                Key = c.Key,
                Value = c.Value,
                ValueType = c.ValueType,
                Description = c.Description
            })
            .ToListAsync(ct);

        var customFields = await _db.CustomFields.IgnoreQueryFilters().AsNoTracking()
            .Where(f => tenantId == null || f.TenantId == tenantId)
            .OrderBy(f => f.DisplayOrder).ThenBy(f => f.Label)
            .Select(f => new CustomFieldViewModel
            {
                Id = f.Id,
                TenantId = f.TenantId,
                FieldKey = f.FieldKey,
                Label = f.Label,
                FieldType = f.FieldType,
                IsRequired = f.IsRequired,
                IsSearchable = f.IsSearchable,
                IsActive = f.IsActive
            })
            .ToListAsync(ct);

        var rules = await _db.RuleDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(r => tenantId == null || r.TenantId == tenantId)
            .OrderBy(r => r.Category).ThenBy(r => r.Priority)
            .Select(r => new RuleItemViewModel
            {
                Id = r.Id,
                TenantId = r.TenantId,
                Name = r.Name,
                Category = r.Category,
                Outcome = r.OutcomeWhenTrue,
                Priority = r.Priority,
                IsActive = r.IsActive,
                Expression = r.Expression
            })
            .ToListAsync(ct);

        var workflowEntities = await _db.WorkflowDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(w => tenantId == null || w.TenantId == tenantId)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        return new TenantConfigurationViewModel
        {
            SelectedTenantId = tenantId,
            Tenants = tenants,
            Configuration = configuration,
            CustomFields = customFields,
            Rules = rules,
            Workflows = workflowEntities.Select(w => new WorkflowItemViewModel
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
            }).ToList(),
            ConfigurationForm = new TenantConfigurationForm { TenantId = tenantId ?? Guid.Empty },
            CustomFieldForm = new CustomFieldForm { TenantId = tenantId ?? Guid.Empty },
            RuleForm = new RuleForm { TenantId = tenantId ?? Guid.Empty },
            WorkflowForm = new WorkflowForm { TenantId = tenantId ?? Guid.Empty }
        };
    }
}

[AllowAnonymous]
[Route("loan-applications")]
public class ApplicationsController : Controller
{
    private readonly AppDbContext _db;

    public ApplicationsController(AppDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? tenantId, LoanApplicationStatus? status, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_db, ct);
        tenantId ??= tenants.FirstOrDefault()?.Id;

        var apps = await _db.LoanApplications.IgnoreQueryFilters().AsNoTracking()
            .Where(a => tenantId == null || a.TenantId == tenantId)
            .Where(a => status == null || a.Status == status)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);

        var tenantLookup = tenants.ToDictionary(t => t.Id, t => t.Name);
        var fields = await _db.CustomFields.IgnoreQueryFilters().AsNoTracking()
            .Where(f => tenantId == null || f.TenantId == tenantId)
            .Where(f => f.IsSearchable)
            .Select(f => new CustomFieldViewModel
            {
                Id = f.Id,
                TenantId = f.TenantId,
                FieldKey = f.FieldKey,
                Label = f.Label,
                FieldType = f.FieldType,
                IsRequired = f.IsRequired,
                IsSearchable = f.IsSearchable,
                IsActive = f.IsActive
            })
            .ToListAsync(ct);

        var workflows = await _db.WorkflowDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(w => tenantId == null || w.TenantId == tenantId)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        return View(new ApplicationsIndexViewModel
        {
            SelectedTenantId = tenantId,
            Status = status,
            Tenants = tenants,
            Applications = apps.Select(a => new ApplicationListItemViewModel
            {
                Id = a.Id,
                ApplicationNumber = a.ApplicationNumber,
                TenantName = tenantLookup.GetValueOrDefault(a.TenantId, "Unknown tenant"),
                RequestedAmount = a.RequestedAmount,
                RequestedTenureMonths = a.RequestedTenureMonths,
                Status = a.Status,
                CurrentStepId = a.CurrentStepId,
                SubmittedAt = a.SubmittedAt
            }).ToList(),
            SearchableFields = fields,
            Workflows = workflows.Select(w => new WorkflowItemViewModel
            {
                Id = w.Id,
                TenantId = w.TenantId,
                Name = w.Name,
                Version = w.Version,
                IsActive = w.IsActive,
                EffectiveFrom = w.EffectiveFrom,
                StepCount = w.GetSteps().Count,
                RoutingRuleCount = w.GetRoutingRules().Count
            }).ToList(),
            Form = new LoanProposalForm { TenantId = tenantId ?? Guid.Empty }
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var tenants = await GetTenantOptionsAsync(_db, ct);
        var tenantLookup = tenants.ToDictionary(t => t.Id, t => t.Name);
        var app = await _db.LoanApplications.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        return View(new ApplicationDetailsViewModel
        {
            Id = app.Id,
            ApplicationNumber = app.ApplicationNumber,
            TenantName = tenantLookup.GetValueOrDefault(app.TenantId, "Unknown tenant"),
            RequestedAmount = app.RequestedAmount,
            RequestedTenureMonths = app.RequestedTenureMonths,
            Status = app.Status,
            CurrentStepId = app.CurrentStepId,
            SubmittedAt = app.SubmittedAt,
            CustomDataJson = app.CustomDataJson
        });
    }

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(LoanProposalForm form, CancellationToken ct)
    {
        try
        {
            ValidateJson(form.CustomDataJson, "Custom data");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        var workflow = await _db.WorkflowDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == form.WorkflowDefinitionId && w.TenantId == form.TenantId, ct);
        if (workflow is null)
        {
            TempData["Error"] = "Select a workflow before creating a loan proposal.";
            return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
        }

        var applicant = await _db.Applicants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == form.TenantId && a.NationalId == form.NationalId, ct)
            ?? Applicant.Create(form.TenantId, form.ApplicantName, form.ApplicantEmail, form.ApplicantPhone, form.NationalId, form.DateOfBirth);
        applicant.UpdateFinancials(form.AnnualIncome, form.CreditScore, form.DebtToIncomeRatio, form.RelationshipYears);
        if (_db.Entry(applicant).State == EntityState.Detached) _db.Applicants.Add(applicant);

        var product = await EnsureDefaultProductAsync(form.TenantId, workflow.Id, ct);

        if (form.Id.HasValue)
        {
            var existing = await _db.LoanApplications.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == form.Id, ct);
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
            var proposal = LoanApplication.Create(
                form.TenantId, product.Id, applicant.Id, workflow.Id, form.RequestedAmount, form.RequestedTenureMonths);
            Set(proposal, nameof(LoanApplication.CurrentStepId), workflow.GetSteps().FirstOrDefault()?.StepId ?? "draft");
            foreach (var item in JsonSerializer.Deserialize<Dictionary<string, object?>>(form.CustomDataJson) ?? [])
            {
                proposal.SetCustomField(item.Key, item.Value);
            }

            proposal.Submit();
            _db.LoanApplications.Add(proposal);
        }

        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Loan proposal saved.";
        return RedirectToAction(nameof(Index), new { tenantId = form.TenantId });
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, Guid tenantId, CancellationToken ct)
    {
        var proposal = await _db.LoanApplications.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (proposal is null) return NotFound();
        _db.LoanApplications.Remove(proposal);
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = "Loan proposal deleted.";
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    [HttpPost("{id:guid}/advance")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Advance(Guid id, Guid tenantId, string action, CancellationToken ct)
    {
        var proposal = await _db.LoanApplications
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (proposal is null) return NotFound();

        switch (action)
        {
            case "send_review":
                proposal.AdvanceTo("branch_review", "mvc", "Sent to branch review");
                TempData["Success"] = "Proposal moved to Branch Review.";
                break;
            case "approve":
                proposal.AdvanceTo("approved", "mvc", "Approved");
                proposal.Approve("mvc", "Approved from MVC");
                TempData["Success"] = "Proposal approved.";
                break;
            case "reject":
                proposal.AdvanceTo("rejected", "mvc", "Rejected");
                proposal.Reject("mvc", "Rejected from MVC");
                TempData["Success"] = "Proposal rejected.";
                break;
            default:
                TempData["Error"] = "Unknown workflow action.";
                break;
        }

        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index), new { tenantId });
    }

    private async Task<LoanProduct> EnsureDefaultProductAsync(Guid tenantId, Guid workflowId, CancellationToken ct)
    {
        var product = await _db.LoanProducts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.WorkflowDefinitionId == workflowId && p.Code == "DEFAULT", ct);
        if (product is not null) return product;

        product = LoanProduct.Create(tenantId, "Default Loan Product", "DEFAULT", LoanProductType.Personal, 1, 999999999, workflowId);
        _db.LoanProducts.Add(product);
        return product;
    }
}

internal static class MvcCrudHelpers
{
    public static async Task<IReadOnlyList<TenantOption>> GetTenantOptionsAsync(AppDbContext db, CancellationToken ct) =>
        await db.Tenants.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TenantOption(t.Id, t.Name, t.Slug, t.DefaultCurrency))
            .ToListAsync(ct);

    public static void Set<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().Name}.");
        property.SetValue(target, value);
    }

    public static T? Deserialize<T>(string json, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{label} contains invalid JSON: {ex.Message}", ex);
        }
    }

    public static void ValidateJson(string json, string label)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{label} contains invalid JSON: {ex.Message}", ex);
        }
    }
}
