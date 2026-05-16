namespace LoanProposal.Core.Entities;

/// <summary>
/// Represents a tenant (bank / financial institution) on the SaaS platform.
/// Tenant registry data lives in the platform database; operational tenant data lives in the tenant database.
/// </summary>
public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string PrimaryColor { get; private set; } = "#0052CC";
    public string LogoUrl { get; private set; } = string.Empty;
    public string DefaultCurrency { get; private set; } = "USD";
    public string DefaultTimezone { get; private set; } = "UTC";
    public string DefaultLocale { get; private set; } = "en-US";
    public string DatabaseName { get; private set; } = string.Empty;
    public string DatabaseConnectionString { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    public IReadOnlyCollection<TenantConfiguration> Configurations { get; private set; } = new List<TenantConfiguration>();
    public IReadOnlyCollection<LoanProduct> LoanProducts { get; private set; } = new List<LoanProduct>();
    public IReadOnlyCollection<CustomField> CustomFields { get; private set; } = new List<CustomField>();
    public IReadOnlyCollection<WorkflowDefinition> WorkflowDefinitions { get; private set; } = new List<WorkflowDefinition>();

    private Tenant() { }

    public static Tenant Create(string name, string slug, string currency = "USD", string timezone = "UTC")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var normalizedSlug = slug.ToLowerInvariant();
        return new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = normalizedSlug,
            DatabaseName = $"loanproposal_{normalizedSlug.Replace("-", "_")}",
            DefaultCurrency = currency,
            DefaultTimezone = timezone,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void ConfigureDatabase(string databaseName, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        DatabaseName = databaseName;
        DatabaseConnectionString = connectionString;
    }

    public void UpdateBranding(string primaryColor, string logoUrl)
    {
        PrimaryColor = primaryColor;
        LogoUrl = logoUrl;
    }

    public void Deactivate() => IsActive = false;
}