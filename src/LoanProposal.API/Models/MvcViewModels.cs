using LoanProposal.Core.Entities;
using LoanProposal.Core.Enums;

namespace LoanProposal.API.Models;

public record TenantOption(Guid Id, string Name, string Slug, string Currency);

public class DashboardViewModel
{
    public IReadOnlyList<TenantSummaryViewModel> Tenants { get; init; } = [];
    public int TotalTenants => Tenants.Count;
    public int TotalApplications => Tenants.Sum(t => t.ApplicationCount);
    public int TotalCustomFields => Tenants.Sum(t => t.CustomFieldCount);
    public int TotalRules => Tenants.Sum(t => t.RuleCount);
}

public class TenantSummaryViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Timezone { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public int ApplicationCount { get; init; }
    public int CustomFieldCount { get; init; }
    public int RuleCount { get; init; }
    public int WorkflowCount { get; init; }
}

public class ApplicationsIndexViewModel
{
    public Guid? SelectedTenantId { get; init; }
    public LoanApplicationStatus? Status { get; init; }
    public string? SearchFieldKey { get; init; }
    public string? SearchValue { get; init; }
    public IReadOnlyList<TenantOption> Tenants { get; init; } = [];
    public IReadOnlyList<ApplicationListItemViewModel> Applications { get; init; } = [];
    public IReadOnlyList<CustomFieldViewModel> SearchableFields { get; init; } = [];
    public IReadOnlyList<WorkflowItemViewModel> Workflows { get; init; } = [];
    public LoanProposalForm Form { get; init; } = new();
}

public class ApplicationListItemViewModel
{
    public Guid Id { get; init; }
    public string ApplicationNumber { get; init; } = string.Empty;
    public string TenantName { get; init; } = string.Empty;
    public decimal RequestedAmount { get; init; }
    public int RequestedTenureMonths { get; init; }
    public LoanApplicationStatus Status { get; init; }
    public string CurrentStepId { get; init; } = string.Empty;
    public DateTime SubmittedAt { get; init; }
}

public class ApplicationDetailsViewModel
{
    public Guid Id { get; init; }
    public string ApplicationNumber { get; init; } = string.Empty;
    public string TenantName { get; init; } = string.Empty;
    public decimal RequestedAmount { get; init; }
    public int RequestedTenureMonths { get; init; }
    public LoanApplicationStatus Status { get; init; }
    public string CurrentStepId { get; init; } = string.Empty;
    public DateTime SubmittedAt { get; init; }
    public string CustomDataJson { get; init; } = "{}";
}

public class TenantConfigurationViewModel
{
    public Guid? SelectedTenantId { get; init; }
    public IReadOnlyList<TenantOption> Tenants { get; init; } = [];
    public IReadOnlyList<ConfigurationItemViewModel> Configuration { get; init; } = [];
    public IReadOnlyList<CustomFieldViewModel> CustomFields { get; init; } = [];
    public IReadOnlyList<RuleItemViewModel> Rules { get; init; } = [];
    public IReadOnlyList<WorkflowItemViewModel> Workflows { get; init; } = [];
    public TenantConfigurationForm ConfigurationForm { get; init; } = new();
    public CustomFieldForm CustomFieldForm { get; init; } = new();
    public RuleForm RuleForm { get; init; } = new();
    public WorkflowForm WorkflowForm { get; init; } = new();
}

public class ConfigurationItemViewModel
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public ConfigValueType ValueType { get; init; }
    public string? Description { get; init; }
}

public class CustomFieldViewModel
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string FieldKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public CustomFieldType FieldType { get; init; }
    public bool IsRequired { get; init; }
    public bool IsSearchable { get; init; }
    public bool IsActive { get; init; }
}

public class RuleItemViewModel
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Name { get; init; } = string.Empty;
    public RuleCategory Category { get; init; }
    public RuleOutcome Outcome { get; init; }
    public int Priority { get; init; }
    public bool IsActive { get; init; }
    public string Expression { get; init; } = string.Empty;
}

public class WorkflowItemViewModel
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; }
    public bool IsActive { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public int StepCount { get; init; }
    public int RoutingRuleCount { get; init; }
    public string StepsJson { get; init; } = "[]";
    public string RoutingRulesJson { get; init; } = "[]";
}

public class TenantListViewModel
{
    public IReadOnlyList<TenantSummaryViewModel> Tenants { get; init; } = [];
    public CreateTenantForm Form { get; init; } = new();
}

public class CreateTenantForm
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public string Timezone { get; set; } = "UTC";
}

public class TenantEditForm : CreateTenantForm
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; } = true;
}

public class TenantConfigurationForm
{
    public Guid? Id { get; set; }
    public Guid TenantId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ConfigValueType ValueType { get; set; } = ConfigValueType.String;
    public string? Description { get; set; }
}

public class CustomFieldForm
{
    public Guid? Id { get; set; }
    public Guid TenantId { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public CustomFieldType FieldType { get; set; } = CustomFieldType.Text;
    public bool IsRequired { get; set; }
    public bool IsSearchable { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RuleForm
{
    public Guid? Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public RuleCategory Category { get; set; } = RuleCategory.Eligibility;
    public string Expression { get; set; } = "{}";
    public RuleOutcome Outcome { get; set; } = RuleOutcome.FlagForReview;
    public int Priority { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}

public class WorkflowForm
{
    public Guid? Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime? EffectiveFrom { get; set; }
    public bool IsActive { get; set; }
    public string StepsJson { get; set; } = """
        [
          { "stepId": "data_entry", "name": "Data Entry", "stepType": "DataEntry", "assigneeRoleCode": "LoanOfficer", "nextStepIds": ["approval"] },
          { "stepId": "approval", "name": "Approval", "stepType": "Approval", "assigneeRoleCode": "BranchManager", "slaHours": 24, "nextStepIds": ["approved"] },
          { "stepId": "approved", "name": "Approved", "stepType": "Terminal", "nextStepIds": [] }
        ]
        """;
    public string RoutingRulesJson { get; set; } = """
        [
          { "fromStepId": "data_entry", "toStepId": "approval", "priority": 1 },
          { "fromStepId": "approval", "toStepId": "approved", "priority": 1 }
        ]
        """;
}

public class LoanProposalForm
{
    public Guid? Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public string ApplicantName { get; set; } = string.Empty;
    public string ApplicantEmail { get; set; } = string.Empty;
    public string ApplicantPhone { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; } = DateTime.Today.AddYears(-30);
    public decimal AnnualIncome { get; set; }
    public int CreditScore { get; set; } = 650;
    public decimal DebtToIncomeRatio { get; set; } = 0.35m;
    public int RelationshipYears { get; set; }
    public decimal RequestedAmount { get; set; }
    public int RequestedTenureMonths { get; set; } = 12;
    public string CustomDataJson { get; set; } = "{}";
}
