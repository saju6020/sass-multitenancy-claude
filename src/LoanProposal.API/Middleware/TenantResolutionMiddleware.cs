using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Services;

namespace LoanProposal.API.Middleware;

/// <summary>
/// Resolves the current tenant from the HTTP request and registers it
/// as ITenantContext for the duration of the request.
///
/// Resolution order:
///   1. JWT claim "tenant_id" (preferred for API access)
///   2. Subdomain: {slug}.loanplatform.io
///   3. X-Tenant-Id header (for machine-to-machine integrations)
///
/// Returns 401 if no tenant can be identified.
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantRepository tenantRepo)
    {
        Guid? tenantId = null;
        string? tenantSlug = null;

        // Strategy 1: JWT claim (set by authentication middleware)
        var jwtClaim = context.User.FindFirst("tenant_id");
        if (jwtClaim is not null && Guid.TryParse(jwtClaim.Value, out var claimTenantId))
        {
            tenantId = claimTenantId;
            tenantSlug = context.User.FindFirst("tenant_slug")?.Value ?? "unknown";
        }

        // Strategy 2: Subdomain
        if (tenantId is null)
        {
            var host = context.Request.Host.Host;
            var parts = host.Split('.');
            if (parts.Length >= 3) // {slug}.loanplatform.io
            {
                var slug = parts[0];
                var tenant = await tenantRepo.GetBySlugAsync(slug);
                if (tenant is not null)
                {
                    tenantId = tenant.Id;
                    tenantSlug = tenant.Slug;
                }
            }
        }

        // Strategy 3: Header
        if (tenantId is null && context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue))
        {
            if (Guid.TryParse(headerValue, out var headerTenantId))
            {
                // In production: verify this tenant ID is valid
                tenantId = headerTenantId;
                tenantSlug = "via-header";
            }
        }

        if (tenantId is null)
        {
            // Platform admin routes bypass tenant resolution
            if (context.Request.Path.StartsWithSegments("/platform"))
            {
                context.RequestServices.GetRequiredService<ITenantContext>();
                await _next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant could not be identified." });
            return;
        }

        // Register the resolved tenant context for this request
        var tenantContext = new HttpTenantContext(tenantId.Value, tenantSlug!);
        context.Items["TenantContext"] = tenantContext;

        await _next(context);
    }
}
