using LoanProposal.Core.Interfaces;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Resolves the current tenant from the HTTP request.
/// Supports three resolution strategies (configured at startup):
///   1. Subdomain: acme-bank.loanplatform.com → slug = "acme-bank"
///   2. JWT Claim: Bearer token contains "tenant_id" claim
///   3. Header: X-Tenant-Id header (for API integrations)
///
/// Registered as Scoped — one instance per HTTP request.
/// </summary>
public class HttpTenantContext : ITenantContext
{
    public Guid TenantId { get; }
    public string TenantSlug { get; }

    public HttpTenantContext(Guid tenantId, string tenantSlug)
    {
        TenantId = tenantId;
        TenantSlug = tenantSlug;
    }
}

/// <summary>
/// System-level tenant context for background jobs and platform operations.
/// Background jobs must explicitly set the tenant they're operating on.
/// </summary>
public class SystemTenantContext : ITenantContext
{
    public Guid TenantId { get; }
    public string TenantSlug { get; }

    public SystemTenantContext(Guid tenantId, string tenantSlug)
    {
        TenantId = tenantId;
        TenantSlug = tenantSlug;
    }

    public static SystemTenantContext ForTenant(Guid tenantId, string slug = "system")
        => new(tenantId, slug);
}

/// <summary>
/// Platform-level context for cross-tenant admin operations.
/// WARNING: Bypasses tenant-scoped query filters. Use only for:
///   - Tenant provisioning
///   - Platform-wide analytics
///   - Admin health checks
/// </summary>
public class PlatformTenantContext : ITenantContext
{
    // Sentinel value — global query filters check for this and allow all tenants
    public Guid TenantId => Guid.Empty;
    public string TenantSlug => "__platform__";
}
