namespace LoanProposal.Core.Entities;

/// <summary>
/// A tenant-configured business rule (eligibility, pricing, or risk flag).
/// Uses a safe expression language (JSONLogic-style) to avoid arbitrary code execution.
/// Rules are versioned and conflict-checked at save time.
/// </summary>
public class RuleDefinition
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public RuleCategory Category { get; private set; }
    public string Expression { get; private set; } = string.Empty;  // JSON Logic expression
    public string? ProductScopeJson { get; private set; }           // null = applies to all products
    public RuleOutcome OutcomeWhenTrue { get; private set; }
    public string? OutcomeData { get; private set; }                // e.g. rate adjustment value
    public int Priority { get; private set; }
    public bool IsActive { get; private set; } = true;
    public int Version { get; private set; } = 1;
    public DateTime CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;

    public Tenant Tenant { get; private set; } = null!;

    private RuleDefinition() { }

    public static RuleDefinition Create(Guid tenantId, string name, RuleCategory category,
        string expression, RuleOutcome outcome, string createdBy, int priority = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        return new RuleDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Category = category,
            Expression = expression,
            OutcomeWhenTrue = outcome,
            Priority = priority,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            Version = 1
        };
    }

    public void ScopeToProducts(IEnumerable<Guid> productIds) =>
        ProductScopeJson = System.Text.Json.JsonSerializer.Serialize(productIds);

    public IEnumerable<Guid>? GetProductScope() =>
        ProductScopeJson is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<IEnumerable<Guid>>(ProductScopeJson);
}

public enum RuleCategory
{
    Eligibility,   // Does the applicant qualify at all?
    Pricing,       // What rate applies?
    RiskFlag,      // Should this go to manual review?
    StepBypass,    // Should a workflow step be skipped?
    AmountAdjust   // Should max loan amount change?
}

public enum RuleOutcome
{
    Decline,
    FlagForReview,
    AdjustRate,
    AdjustMaxAmount,
    BypassStep,
    RequireAdditionalDocument
}
