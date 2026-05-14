using System.Text.Json;
using LoanProposal.Core.Enums;

namespace LoanProposal.Core.Entities;

/// <summary>
/// The core aggregate of the system — a loan application submitted by a borrower.
/// Scoped strictly to a TenantId. Locks to a specific WorkflowDefinition version at submission.
/// </summary>
public class LoanApplication
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ApplicationNumber { get; private set; } = string.Empty;
    public Guid LoanProductId { get; private set; }
    public Guid ApplicantId { get; private set; }

    /// <summary>The specific workflow version governing this application — immutable after submission.</summary>
    public Guid WorkflowDefinitionId { get; private set; }

    public decimal RequestedAmount { get; private set; }
    public int RequestedTenureMonths { get; private set; }
    public LoanApplicationStatus Status { get; private set; }
    public string CurrentStepId { get; private set; } = string.Empty;
    public DateTime SubmittedAt { get; private set; }
    public DateTime? DecisionAt { get; private set; }

    /// <summary>
    /// JSONB blob storing both standard extended fields and tenant custom field values.
    /// Key = field key (e.g. "gst_registration_number"), Value = serialized value.
    /// All custom fields from the tenant's CustomField registry are stored here.
    /// </summary>
    public string CustomDataJson { get; private set; } = "{}";

    public Tenant Tenant { get; private set; } = null!;
    public LoanProduct LoanProduct { get; private set; } = null!;
    public Applicant Applicant { get; private set; } = null!;
    public WorkflowDefinition WorkflowDefinition { get; private set; } = null!;

    public IReadOnlyCollection<ApplicationStateTransition> StateTransitions { get; private set; } = new List<ApplicationStateTransition>();
    public IReadOnlyCollection<ApplicationDocument> Documents { get; private set; } = new List<ApplicationDocument>();

    private LoanApplication() { }

    public static LoanApplication Create(Guid tenantId, Guid loanProductId, Guid applicantId,
        Guid workflowDefinitionId, decimal requestedAmount, int requestedTenureMonths,
        string applicationNumberPrefix = "LA")
    {
        return new LoanApplication
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LoanProductId = loanProductId,
            ApplicantId = applicantId,
            WorkflowDefinitionId = workflowDefinitionId,
            RequestedAmount = requestedAmount,
            RequestedTenureMonths = requestedTenureMonths,
            Status = LoanApplicationStatus.Draft,
            ApplicationNumber = $"{applicationNumberPrefix}-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpper()}",
            SubmittedAt = DateTime.UtcNow
        };
    }

    // ── Custom field accessors ──────────────────────────────────────────────

    public void SetCustomField(string fieldKey, object? value)
    {
        var data = GetCustomData();
        data[fieldKey] = value is null ? null : JsonSerializer.SerializeToElement(value);
        CustomDataJson = JsonSerializer.Serialize(data);
    }

    public T? GetCustomField<T>(string fieldKey)
    {
        var data = GetCustomData();
        if (!data.TryGetValue(fieldKey, out var element)) return default;
        return element.HasValue ? element.Value.Deserialize<T>() : default;
    }

    private Dictionary<string, JsonElement?> GetCustomData() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(CustomDataJson)
        ?? new Dictionary<string, JsonElement?>();

    // ── State machine transitions ───────────────────────────────────────────

    public void AdvanceTo(string nextStepId, string performedBy, string? comments = null)
    {
        if (Status == LoanApplicationStatus.Approved || Status == LoanApplicationStatus.Rejected)
            throw new InvalidOperationException("Cannot advance a finalized application.");

        CurrentStepId = nextStepId;
    }

    public void Approve(string approvedBy, string? comments = null)
    {
        Status = LoanApplicationStatus.Approved;
        DecisionAt = DateTime.UtcNow;
    }

    public void Reject(string rejectedBy, string reason)
    {
        Status = LoanApplicationStatus.Rejected;
        DecisionAt = DateTime.UtcNow;
    }

    public void Submit()
    {
        if (Status != LoanApplicationStatus.Draft)
            throw new InvalidOperationException("Only Draft applications can be submitted.");
        Status = LoanApplicationStatus.UnderReview;
    }
}
