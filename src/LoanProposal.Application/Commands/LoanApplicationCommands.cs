using LoanProposal.Core.Entities;
using LoanProposal.Core.Enums;
using LoanProposal.Core.Interfaces;
using MediatR;

namespace LoanProposal.Application.Commands;

// ── Submit Loan Application ─────────────────────────────────────────────────

public record SubmitLoanApplicationCommand(
    Guid LoanProductId,
    Guid ApplicantId,
    decimal RequestedAmount,
    int RequestedTenureMonths,
    Dictionary<string, object?> CustomFields   // Tenant-defined fields
) : IRequest<SubmitLoanApplicationResult>;

public record SubmitLoanApplicationResult(
    bool Success,
    string? ApplicationNumber,
    string? ErrorMessage,
    IReadOnlyList<string>? RuleViolations
);

public class SubmitLoanApplicationHandler : IRequestHandler<SubmitLoanApplicationCommand, SubmitLoanApplicationResult>
{
    private readonly ITenantContext _tenantContext;
    private readonly ILoanApplicationRepository _appRepo;
    private readonly ILoanProductRepository _productRepo;
    private readonly IWorkflowDefinitionRepository _workflowRepo;
    private readonly IRuleEngine _ruleEngine;
    private readonly ICustomFieldRepository _customFieldRepo;

    public SubmitLoanApplicationHandler(
        ITenantContext tenantContext,
        ILoanApplicationRepository appRepo,
        ILoanProductRepository productRepo,
        IWorkflowDefinitionRepository workflowRepo,
        IRuleEngine ruleEngine,
        ICustomFieldRepository customFieldRepo)
    {
        _tenantContext = tenantContext;
        _appRepo = appRepo;
        _productRepo = productRepo;
        _workflowRepo = workflowRepo;
        _ruleEngine = ruleEngine;
        _customFieldRepo = customFieldRepo;
    }

    public async Task<SubmitLoanApplicationResult> Handle(
        SubmitLoanApplicationCommand request, CancellationToken ct)
    {
        // 1. Validate custom fields against tenant's CustomField registry
        var registeredFields = await _customFieldRepo.GetAllAsync(ct);
        var unknownFields = request.CustomFields.Keys
            .Except(registeredFields.Select(f => f.FieldKey))
            .ToList();

        if (unknownFields.Count != 0)
            return new SubmitLoanApplicationResult(false, null,
                $"Unknown custom fields: {string.Join(", ", unknownFields)}", null);

        var requiredMissing = registeredFields
            .Where(f => f.IsRequired && !request.CustomFields.ContainsKey(f.FieldKey))
            .Select(f => f.Label)
            .ToList();

        if (requiredMissing.Count != 0)
            return new SubmitLoanApplicationResult(false, null,
                $"Required fields missing: {string.Join(", ", requiredMissing)}", null);

        // 2. Resolve the active workflow for this tenant's selected product
        // Product lookup is tenant-scoped, so tenant workflow variation is selected here
        // For now using a placeholder — real implementation loads from product
        // and the application is locked to that product's active workflow version.
        var product = await _productRepo.GetActiveByIdAsync(request.LoanProductId, ct);
        if (product is null)
            return new SubmitLoanApplicationResult(false, null,
                "Loan product not found for the current tenant.", null);

        if (request.RequestedAmount < product.MinAmount || request.RequestedAmount > product.MaxAmount)
            return new SubmitLoanApplicationResult(false, null,
                $"Requested amount must be between {product.MinAmount} and {product.MaxAmount}.", null);

        var workflow = await _workflowRepo.GetActiveVersionAsync(product.WorkflowDefinitionId, ct);
        if (workflow is null)
            return new SubmitLoanApplicationResult(false, null,
                "No active workflow is configured for the selected loan product.", null);

        var firstStep = workflow.GetSteps().FirstOrDefault();
        if (firstStep is null)
            return new SubmitLoanApplicationResult(false, null,
                "The selected loan product workflow has no steps configured.", null);

        // 3. Create the application (locked to current workflow version)
        var application = LoanApplication.Create(
            _tenantContext.TenantId,
            request.LoanProductId,
            request.ApplicantId,
            workflow.Id,
            request.RequestedAmount,
            request.RequestedTenureMonths
        );

        // 4. Store custom field values
        foreach (var (key, value) in request.CustomFields)
            application.SetCustomField(key, value);

        // 5. Evaluate eligibility rules
        // Full context requires applicant data — omitted for brevity in this blueprint
        // In production: load applicant, build LoanApplicationContext, run ruleEngine

        // 6. Submit and persist
        application.AdvanceTo(firstStep.StepId, "system", "Initialized at workflow start.");
        application.Submit();
        await _appRepo.AddAsync(application, ct);
        await _appRepo.SaveChangesAsync(ct);

        return new SubmitLoanApplicationResult(true, application.ApplicationNumber, null, null);
    }
}

// ── Advance Application Workflow ────────────────────────────────────────────

public record AdvanceWorkflowCommand(
    Guid ApplicationId,
    string Action,          // Approve | Reject | RequestInfo | Escalate
    string? Comments
) : IRequest<AdvanceWorkflowResult>;

public record AdvanceWorkflowResult(bool Success, string? NextStep, string? ErrorMessage);

public class AdvanceWorkflowHandler : IRequestHandler<AdvanceWorkflowCommand, AdvanceWorkflowResult>
{
    private readonly ILoanApplicationRepository _appRepo;
    private readonly IWorkflowEngine _workflowEngine;
    private readonly ITenantContext _tenantContext;

    public AdvanceWorkflowHandler(
        ILoanApplicationRepository appRepo,
        IWorkflowEngine workflowEngine,
        ITenantContext tenantContext)
    {
        _appRepo = appRepo;
        _workflowEngine = workflowEngine;
        _tenantContext = tenantContext;
    }

    public async Task<AdvanceWorkflowResult> Handle(AdvanceWorkflowCommand request, CancellationToken ct)
    {
        var application = await _appRepo.GetByIdAsync(request.ApplicationId, ct);
        if (application is null)
            return new AdvanceWorkflowResult(false, null, "Application not found.");

        var result = await _workflowEngine.AdvanceAsync(
            application, request.Action, "current-user", request.Comments, ct);

        if (result.Success)
            await _appRepo.SaveChangesAsync(ct);

        return new AdvanceWorkflowResult(result.Success, result.NextStepId, result.ErrorMessage);
    }
}

// ── Configure Tenant Workflow ───────────────────────────────────────────────

public record ConfigureWorkflowCommand(
    string WorkflowName,
    IReadOnlyList<WorkflowStepDefinition> Steps,
    IReadOnlyList<RoutingRule> RoutingRules,
    DateTime? EffectiveFrom
) : IRequest<ConfigureWorkflowResult>;

public record ConfigureWorkflowResult(bool Success, Guid? WorkflowId, string? ErrorMessage);

public class ConfigureWorkflowHandler : IRequestHandler<ConfigureWorkflowCommand, ConfigureWorkflowResult>
{
    private readonly IWorkflowDefinitionRepository _workflowRepo;
    private readonly ITenantContext _tenantContext;

    public ConfigureWorkflowHandler(IWorkflowDefinitionRepository workflowRepo, ITenantContext tenantContext)
    {
        _workflowRepo = workflowRepo;
        _tenantContext = tenantContext;
    }

    public async Task<ConfigureWorkflowResult> Handle(ConfigureWorkflowCommand request, CancellationToken ct)
    {
        var workflow = WorkflowDefinition.Create(
            _tenantContext.TenantId,
            request.WorkflowName,
            "current-user",
            request.EffectiveFrom
        );

        try
        {
            workflow.SetSteps(request.Steps);
            workflow.SetRoutingRules(request.RoutingRules);
        }
        catch (InvalidOperationException ex)
        {
            return new ConfigureWorkflowResult(false, null, ex.Message);
        }

        // Workflow starts inactive — must be explicitly activated after review
        await _workflowRepo.AddAsync(workflow, ct);
        await _workflowRepo.SaveChangesAsync(ct);

        return new ConfigureWorkflowResult(true, workflow.Id, null);
    }
}
