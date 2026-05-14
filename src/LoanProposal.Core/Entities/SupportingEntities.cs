namespace LoanProposal.Core.Entities;

public class Applicant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string NationalId { get; private set; } = string.Empty;
    public DateTime DateOfBirth { get; private set; }
    public decimal? AnnualIncome { get; private set; }
    public int? CreditScore { get; private set; }
    public decimal? DebtToIncomeRatio { get; private set; }

    /// <summary>Number of years the applicant has been a customer of this tenant.</summary>
    public int RelationshipYears { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public Tenant Tenant { get; private set; } = null!;

    private Applicant() { }

    public static Applicant Create(Guid tenantId, string fullName, string email,
        string phoneNumber, string nationalId, DateTime dateOfBirth)
    {
        return new Applicant
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FullName = fullName,
            Email = email,
            PhoneNumber = phoneNumber,
            NationalId = nationalId,
            DateOfBirth = dateOfBirth,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateFinancials(decimal annualIncome, int creditScore, decimal dtiRatio, int relationshipYears)
    {
        AnnualIncome = annualIncome;
        CreditScore = creditScore;
        DebtToIncomeRatio = dtiRatio;
        RelationshipYears = relationshipYears;
    }
}

/// <summary>Immutable audit trail of every state change on a loan application.</summary>
public class ApplicationStateTransition
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ApplicationId { get; private set; }
    public string FromStepId { get; private set; } = string.Empty;
    public string ToStepId { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;      // Approved, Rejected, Escalated, etc.
    public string PerformedBy { get; private set; } = string.Empty;
    public string? Comments { get; private set; }
    public DateTime PerformedAt { get; private set; }

    public LoanApplication Application { get; private set; } = null!;

    private ApplicationStateTransition() { }

    public static ApplicationStateTransition Record(Guid tenantId, Guid applicationId,
        string fromStep, string toStep, string action, string performedBy, string? comments = null)
    {
        return new ApplicationStateTransition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ApplicationId = applicationId,
            FromStepId = fromStep,
            ToStepId = toStep,
            Action = action,
            PerformedBy = performedBy,
            Comments = comments,
            PerformedAt = DateTime.UtcNow
        };
    }
}

/// <summary>A document attached to a loan application.</summary>
public class ApplicationDocument
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ApplicationId { get; private set; }
    public string DocumentType { get; private set; } = string.Empty;   // e.g. "IncomeStatement", "Passport"
    public string FileName { get; private set; } = string.Empty;
    public string StorageKey { get; private set; } = string.Empty;     // Blob storage key
    public string ContentType { get; private set; } = string.Empty;
    public long FileSizeBytes { get; private set; }
    public bool IsGenerated { get; private set; }                       // True for sanction letters etc.
    public Guid? TemplateVersionId { get; private set; }               // Which template generated it
    public DateTime UploadedAt { get; private set; }
    public string UploadedBy { get; private set; } = string.Empty;

    public LoanApplication Application { get; private set; } = null!;

    private ApplicationDocument() { }

    public static ApplicationDocument Create(Guid tenantId, Guid applicationId,
        string documentType, string fileName, string storageKey, string contentType,
        long fileSizeBytes, string uploadedBy)
    {
        return new ApplicationDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ApplicationId = applicationId,
            DocumentType = documentType,
            FileName = fileName,
            StorageKey = storageKey,
            ContentType = contentType,
            FileSizeBytes = fileSizeBytes,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = uploadedBy
        };
    }
}
