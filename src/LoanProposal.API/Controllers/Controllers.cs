using LoanProposal.Application.Commands;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoanProposal.API.Controllers;

/// <summary>
/// Loan application lifecycle endpoints.
/// All endpoints are scoped to the authenticated tenant via ITenantContext.
/// </summary>
[ApiController]
[Route("api/applications")]
[Authorize]
public class LoanApplicationsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILoanApplicationRepository _appRepo;
    private readonly ITenantContext _tenantContext;

    public LoanApplicationsController(IMediator mediator,
        ILoanApplicationRepository appRepo, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _appRepo = appRepo;
        _tenantContext = tenantContext;
    }

    /// <summary>Submit a new loan application.</summary>
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitApplicationRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new SubmitLoanApplicationCommand(
            request.LoanProductId,
            request.ApplicantId,
            request.RequestedAmount,
            request.RequestedTenureMonths,
            request.CustomFields
        ), ct);

        if (!result.Success)
            return BadRequest(new { result.ErrorMessage, result.RuleViolations });

        return Ok(new { result.ApplicationNumber, message = "Application submitted successfully." });
    }

    /// <summary>Get a specific application (tenant-scoped — cannot access other tenants' data).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var app = await _appRepo.GetByIdAsync(id, ct);
        if (app is null) return NotFound();
        return Ok(app);
    }

    /// <summary>Get all applications for the current tenant in a specific status.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Core.Enums.LoanApplicationStatus? status, CancellationToken ct)
    {
        var apps = status.HasValue
            ? await _appRepo.GetByStatusAsync(status.Value, ct)
            : await _appRepo.GetAllAsync(ct);
        return Ok(apps);
    }

    /// <summary>Advance an application through the configured workflow.</summary>
    [HttpPost("{id:guid}/advance")]
    public async Task<IActionResult> Advance(Guid id, [FromBody] AdvanceRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new AdvanceWorkflowCommand(id, request.Action, request.Comments), ct);

        if (!result.Success)
            return BadRequest(new { result.ErrorMessage });

        return Ok(new { result.NextStep, message = $"Application advanced to {result.NextStep}." });
    }

    /// <summary>Search by tenant-defined custom field value.</summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchByCustomField(
        [FromQuery] string fieldKey, [FromQuery] string value, CancellationToken ct)
    {
        var apps = await _appRepo.SearchByCustomFieldAsync(fieldKey, value, ct);
        return Ok(apps);
    }
}

/// <summary>
/// Tenant self-service configuration endpoints.
/// Allows authorized tenant admins to configure their own workflows, rules, and custom fields
/// without involving platform developers.
/// </summary>
[ApiController]
[Route("api/configuration")]
[Authorize(Roles = "TenantAdmin")]
public class TenantConfigurationController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICustomFieldRepository _customFieldRepo;
    private readonly IRuleDefinitionRepository _ruleRepo;

    public TenantConfigurationController(IMediator mediator,
        ICustomFieldRepository customFieldRepo, IRuleDefinitionRepository ruleRepo)
    {
        _mediator = mediator;
        _customFieldRepo = customFieldRepo;
        _ruleRepo = ruleRepo;
    }

    // ── Custom Fields ───────────────────────────────────────────────────────

    [HttpGet("custom-fields")]
    public async Task<IActionResult> GetCustomFields(CancellationToken ct) =>
        Ok(await _customFieldRepo.GetAllAsync(ct));

    [HttpPost("custom-fields")]
    public async Task<IActionResult> CreateCustomField([FromBody] CreateCustomFieldRequest request, CancellationToken ct)
    {
        // Handled by a command handler (omitted for brevity)
        return Ok(new { message = "Custom field registered.", request.FieldKey });
    }

    // ── Workflow Configuration ──────────────────────────────────────────────

    [HttpPost("workflows")]
    public async Task<IActionResult> ConfigureWorkflow([FromBody] ConfigureWorkflowCommand command, CancellationToken ct)
    {
        var result = await _mediator.Send(command, ct);
        if (!result.Success)
            return BadRequest(new { result.ErrorMessage });

        return Ok(new { result.WorkflowId, message = "Workflow saved (inactive). Activate when ready." });
    }

    [HttpPost("workflows/{id:guid}/activate")]
    public async Task<IActionResult> ActivateWorkflow(Guid id, [FromBody] ActivateWorkflowRequest request,
        CancellationToken ct)
    {
        // In production: load workflow, call workflow.Activate(), save
        return Ok(new { message = "Workflow activated.", effectiveFrom = request.EffectiveFrom });
    }

    // ── Rule Configuration ──────────────────────────────────────────────────

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] Core.Entities.RuleCategory? category, CancellationToken ct)
    {
        var rules = category.HasValue
            ? await _ruleRepo.GetByCategoryAsync(category.Value, ct)
            : await _ruleRepo.GetAllAsync(ct);
        return Ok(rules);
    }

    [HttpPost("rules/validate")]
    public IActionResult ValidateRule([FromBody] ValidateRuleRequest request)
    {
        // Pre-save validation: check expression syntax and conflict detection
        // In production: parse the JSON Logic expression, check for operator precedence issues,
        // scan existing rules for conflicts using Z3 or simple overlap detection
        return Ok(new { isValid = true, conflicts = Array.Empty<string>(), warnings = Array.Empty<string>() });
    }
}

/// <summary>
/// Platform-level endpoints for tenant provisioning and admin.
/// Protected by platform-admin role — not accessible to tenant users.
/// </summary>
[ApiController]
[Route("platform/tenants")]
[Authorize(Roles = "PlatformAdmin")]
public class PlatformTenantsController : ControllerBase
{
    private readonly ITenantRepository _tenantRepo;

    public PlatformTenantsController(ITenantRepository tenantRepo) => _tenantRepo = tenantRepo;

    [HttpPost]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var existingBySlug = await _tenantRepo.GetBySlugAsync(request.Slug, ct);
        if (existingBySlug is not null)
            return Conflict(new { error = $"Slug '{request.Slug}' is already taken." });

        var tenant = Tenant.Create(request.Name, request.Slug, request.Currency, request.Timezone);
        await _tenantRepo.AddAsync(tenant, ct);
        await _tenantRepo.SaveChangesAsync(ct);

        return Ok(new { tenant.Id, tenant.Slug, message = "Tenant provisioned." });
    }
}

// ── Request DTOs ─────────────────────────────────────────────────────────────

public record SubmitApplicationRequest(
    Guid LoanProductId,
    Guid ApplicantId,
    decimal RequestedAmount,
    int RequestedTenureMonths,
    Dictionary<string, object?> CustomFields
);

public record AdvanceRequest(string Action, string? Comments);
public record ActivateWorkflowRequest(DateTime? EffectiveFrom);
public record CreateCustomFieldRequest(string FieldKey, string Label, Core.Entities.CustomFieldType FieldType, bool IsRequired);
public record ValidateRuleRequest(string Expression, Core.Entities.RuleCategory Category);
public record CreateTenantRequest(string Name, string Slug, string Currency, string Timezone);
