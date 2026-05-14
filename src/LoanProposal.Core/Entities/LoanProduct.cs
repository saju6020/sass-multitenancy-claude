namespace LoanProposal.Core.Entities;

/// <summary>
/// A loan product configured by the tenant (e.g. "SME Fast Track", "Home Loan", "Auto Loan").
/// Ties together workflow, pricing rules, and document templates.
/// </summary>
public class LoanProduct
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public LoanProductType ProductType { get; private set; }
    public decimal MinAmount { get; private set; }
    public decimal MaxAmount { get; private set; }
    public decimal MinTenureMonths { get; private set; }
    public decimal MaxTenureMonths { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>Which workflow governs applications for this product</summary>
    public Guid WorkflowDefinitionId { get; private set; }

    /// <summary>Which document template to use for sanction letters</summary>
    public Guid? SanctionTemplateId { get; private set; }

    public Tenant Tenant { get; private set; } = null!;
    public WorkflowDefinition WorkflowDefinition { get; private set; } = null!;

    private LoanProduct() { }

    public static LoanProduct Create(Guid tenantId, string name, string code,
        LoanProductType productType, decimal minAmount, decimal maxAmount,
        Guid workflowDefinitionId)
    {
        if (minAmount >= maxAmount)
            throw new ArgumentException("MinAmount must be less than MaxAmount.");

        return new LoanProduct
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Code = code.ToUpperInvariant(),
            ProductType = productType,
            MinAmount = minAmount,
            MaxAmount = maxAmount,
            WorkflowDefinitionId = workflowDefinitionId
        };
    }
}

public enum LoanProductType
{
    Personal,
    SME,
    Commercial,
    Home,
    Auto,
    IslamicFinance,
    GovernmentBacked,
    Microfinance
}
