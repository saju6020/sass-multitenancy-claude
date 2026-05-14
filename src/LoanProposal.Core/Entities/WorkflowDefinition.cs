using System.Text.Json;

namespace LoanProposal.Core.Entities;

/// <summary>
/// Defines the approval workflow for a loan product.
/// Versioned so in-flight applications are governed by the version active at submission time.
/// </summary>
public class WorkflowDefinition
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int Version { get; private set; } = 1;
    public bool IsActive { get; private set; }
    public DateTime? EffectiveFrom { get; private set; }   // null = immediately
    public DateTime? EffectiveTo { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;

    /// <summary>
    /// JSON-serialized list of WorkflowStep definitions.
    /// Stored as JSON to allow schema evolution without migrations.
    /// </summary>
    public string StepsJson { get; private set; } = "[]";

    /// <summary>
    /// JSON-serialized routing rules (conditional branching).
    /// e.g. "if LoanAmount > 500000 → route to CreditCommittee step"
    /// </summary>
    public string RoutingRulesJson { get; private set; } = "[]";

    public Tenant Tenant { get; private set; } = null!;

    private WorkflowDefinition() { }

    public static WorkflowDefinition Create(Guid tenantId, string name, string createdBy,
        DateTime? effectiveFrom = null)
    {
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Version = 1,
            IsActive = false, // Must be explicitly activated
            EffectiveFrom = effectiveFrom,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void SetSteps(IEnumerable<WorkflowStepDefinition> steps)
    {
        ValidateSteps(steps);
        StepsJson = JsonSerializer.Serialize(steps, JsonSerializerDefaults());
    }

    public IReadOnlyList<WorkflowStepDefinition> GetSteps() =>
        JsonSerializer.Deserialize<List<WorkflowStepDefinition>>(StepsJson, JsonSerializerDefaults())!;

    public void SetRoutingRules(IEnumerable<RoutingRule> rules) =>
        RoutingRulesJson = JsonSerializer.Serialize(rules, JsonSerializerDefaults());

    public IReadOnlyList<RoutingRule> GetRoutingRules() =>
        JsonSerializer.Deserialize<List<RoutingRule>>(RoutingRulesJson, JsonSerializerDefaults())!;

    public void Activate(DateTime? effectiveFrom = null)
    {
        IsActive = true;
        EffectiveFrom = effectiveFrom ?? DateTime.UtcNow;
    }

    public WorkflowDefinition CreateNewVersion(string createdBy)
    {
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Name = Name,
            Version = Version + 1,
            IsActive = false,
            StepsJson = StepsJson,
            RoutingRulesJson = RoutingRulesJson,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    private static void ValidateSteps(IEnumerable<WorkflowStepDefinition> steps)
    {
        var stepList = steps.ToList();
        if (!stepList.Any())
            throw new InvalidOperationException("Workflow must have at least one step.");

        var hasTerminal = stepList.Any(s => s.StepType == WorkflowStepType.Terminal);
        if (!hasTerminal)
            throw new InvalidOperationException("Workflow must have at least one terminal step (Approved/Rejected).");
    }

    private static JsonSerializerOptions JsonSerializerDefaults() =>
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

/// <summary>
/// A single step within a workflow definition.
/// </summary>
public class WorkflowStepDefinition
{
    public string StepId { get; set; } = string.Empty;           // e.g. "branch_manager_review"
    public string Name { get; set; } = string.Empty;
    public WorkflowStepType StepType { get; set; }
    public string? AssigneeRoleCode { get; set; }                // Role that owns this step
    public int? SlaHours { get; set; }                           // SLA for this specific step
    public int? QuorumRequired { get; set; }                     // For committee votes
    public int? QuorumOf { get; set; }                           // e.g. 3 of 5
    public bool AllowDelegation { get; set; }
    public string? BypassConditionExpression { get; set; }       // Skip this step if condition true
    public string? EscalationRoleCode { get; set; }             // Where to escalate on SLA breach
    public int? EscalationAfterHours { get; set; }
    public IList<string> NextStepIds { get; set; } = [];        // Possible next steps
}

/// <summary>Conditional routing rule between workflow steps.</summary>
public class RoutingRule
{
    public string FromStepId { get; set; } = string.Empty;
    public string ToStepId { get; set; } = string.Empty;
    public string? ConditionExpression { get; set; }             // e.g. "LoanAmount > 500000"
    public int Priority { get; set; }                            // Lower = evaluated first
    public string? Description { get; set; }
}

public enum WorkflowStepType
{
    DataEntry,
    DocumentUpload,
    Approval,
    CommitteeVote,
    AutomatedCheck,
    Integration,    // External API call
    Terminal        // Approved / Rejected / Cancelled
}
