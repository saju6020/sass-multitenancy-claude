using LoanProposal.Core.Entities;

namespace LoanProposal.Core.Interfaces;

// Ã¢â€â‚¬Ã¢â€â‚¬ Tenant Context Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

/// <summary>
/// Injected into every service to provide the current tenant without
/// passing TenantId through every method call.
/// Resolved from the HTTP request (subdomain, JWT claim, or header).
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    string TenantSlug { get; }
    string ConnectionString { get; }
}
// Ã¢â€â‚¬Ã¢â€â‚¬ Repositories Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>All queries are automatically scoped to the current tenant.</summary>
public interface ITenantScopedRepository<T> : IRepository<T> where T : class { }

public interface ILoanApplicationRepository : ITenantScopedRepository<LoanApplication>
{
    Task<LoanApplication?> GetByApplicationNumberAsync(string applicationNumber, CancellationToken ct = default);
    Task<IReadOnlyList<LoanApplication>> GetByStatusAsync(Enums.LoanApplicationStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<LoanApplication>> GetByApplicantAsync(Guid applicantId, CancellationToken ct = default);

    /// <summary>Custom field aware query Ã¢â‚¬â€ tenant's field keys resolved at runtime.</summary>
    Task<IReadOnlyList<LoanApplication>> SearchByCustomFieldAsync(
        string fieldKey, string value, CancellationToken ct = default);
}

public interface ILoanProductRepository : ITenantScopedRepository<LoanProduct>
{
    Task<LoanProduct?> GetActiveByIdAsync(Guid id, CancellationToken ct = default);
}

public interface ITenantRepository : IRepository<Tenant>
{
    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<TenantConfiguration?> GetConfigAsync(Guid tenantId, string key, CancellationToken ct = default);
    Task SetConfigAsync(TenantConfiguration config, CancellationToken ct = default);
}

public interface IWorkflowDefinitionRepository : ITenantScopedRepository<WorkflowDefinition>
{
    Task<WorkflowDefinition?> GetActiveVersionAsync(Guid workflowId, CancellationToken ct = default);
    Task<WorkflowDefinition?> GetVersionAsync(Guid workflowId, int version, CancellationToken ct = default);

    /// <summary>Returns the workflow version that was active at a given point in time.</summary>
    Task<WorkflowDefinition?> GetVersionActiveAtAsync(Guid workflowId, DateTime pointInTime, CancellationToken ct = default);
}

public interface IRuleDefinitionRepository : ITenantScopedRepository<RuleDefinition>
{
    Task<IReadOnlyList<RuleDefinition>> GetByCategoryAsync(Entities.RuleCategory category, CancellationToken ct = default);
    Task<IReadOnlyList<RuleDefinition>> GetApplicableToProductAsync(Guid productId, CancellationToken ct = default);
}

public interface ICustomFieldRepository : ITenantScopedRepository<CustomField>
{
    Task<CustomField?> GetByFieldKeyAsync(string fieldKey, CancellationToken ct = default);
    Task<IReadOnlyList<CustomField>> GetSearchableFieldsAsync(CancellationToken ct = default);
}

// Ã¢â€â‚¬Ã¢â€â‚¬ Domain Services Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

public interface IRuleEngine
{
    /// <summary>
    /// Evaluates all active rules of a given category against a loan application context.
    /// Returns outcomes for all rules that evaluated to true, in priority order.
    /// </summary>
    Task<IReadOnlyList<RuleEvaluationResult>> EvaluateAsync(
        RuleCategory category,
        LoanApplicationContext context,
        Guid? productId = null,
        CancellationToken ct = default);
}

public interface IWorkflowEngine
{
    Task<WorkflowAdvanceResult> AdvanceAsync(
        LoanApplication application, string action, string performedBy,
        string? comments = null, CancellationToken ct = default);

    Task<string> ResolveNextStepAsync(
        LoanApplication application, WorkflowDefinition workflow,
        CancellationToken ct = default);
}

public interface IDocumentGenerator
{
    Task<byte[]> GenerateAsync(string templateId, LoanApplicationContext context,
        string language = "en", CancellationToken ct = default);
}

public interface INotificationService
{
    Task SendAsync(NotificationRequest request, CancellationToken ct = default);
}

// Ã¢â€â‚¬Ã¢â€â‚¬ Value objects used by interfaces Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

/// <summary>
/// The unified data context passed to rules, templates, and workflow routing.
/// Includes both standard fields and custom field values Ã¢â‚¬â€ all keyed the same way.
/// This is the "shared context model" from the architecture document.
/// </summary>
public record LoanApplicationContext(
    LoanApplication Application,
    Applicant Applicant,
    LoanProduct Product,
    IReadOnlyDictionary<string, object?> CustomFields
);

public record RuleEvaluationResult(
    Guid RuleId,
    string RuleName,
    bool Passed,
    RuleOutcome? Outcome,
    string? OutcomeData,
    string Expression
);

public record WorkflowAdvanceResult(
    bool Success,
    string? NextStepId,
    string? ErrorMessage,
    IReadOnlyList<string> NotificationsTriggered
);

public record NotificationRequest(
    Guid TenantId,
    string Channel,  // email | sms | whatsapp | inapp
    string RecipientId,
    string TemplateKey,
    IReadOnlyDictionary<string, string> Variables,
    string Language = "en"
);
