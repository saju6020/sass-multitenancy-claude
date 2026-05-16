using LoanProposal.Core.Interfaces;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Resolves the current tenant from the HTTP request and carries the tenant database connection.
/// </summary>
public class HttpTenantContext : ITenantContext
{
    public Guid TenantId { get; }
    public string TenantSlug { get; }
    public string ConnectionString { get; }

    public HttpTenantContext(Guid tenantId, string tenantSlug, string connectionString)
    {
        TenantId = tenantId;
        TenantSlug = tenantSlug;
        ConnectionString = connectionString;
    }
}

/// <summary>
/// System-level tenant context for background jobs.
/// </summary>
public class SystemTenantContext : ITenantContext
{
    public Guid TenantId { get; }
    public string TenantSlug { get; }
    public string ConnectionString { get; }

    public SystemTenantContext(Guid tenantId, string tenantSlug, string connectionString)
    {
        TenantId = tenantId;
        TenantSlug = tenantSlug;
        ConnectionString = connectionString;
    }

    public static SystemTenantContext ForTenant(Guid tenantId, string slug, string connectionString)
        => new(tenantId, slug, connectionString);
}

/// <summary>
/// Platform-level context for cross-tenant admin operations. It has no tenant database.
/// </summary>
public class PlatformTenantContext : ITenantContext
{
    public Guid TenantId => Guid.Empty;
    public string TenantSlug => "__platform__";
    public string ConnectionString => string.Empty;
}