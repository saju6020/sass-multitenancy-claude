namespace TenantRegistration.API.Models;

public class TenantListViewModel
{
    public IReadOnlyList<TenantSummaryViewModel> Tenants { get; init; } = [];
    public CreateTenantForm Form { get; init; } = new();
}

public class TenantSummaryViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Timezone { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
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
    public string DatabaseName { get; set; } = string.Empty;
    public string DatabaseConnectionString { get; set; } = string.Empty;
}
