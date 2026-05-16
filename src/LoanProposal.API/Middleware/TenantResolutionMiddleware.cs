using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Data;
using LoanProposal.Infrastructure.Services;

namespace LoanProposal.API.Middleware;

/// <summary>
/// Resolves the current tenant and attaches its database connection string to ITenantContext.
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantRepository tenantRepo, TenantDbContextFactory tenantDbFactory)
    {
        Tenant? tenant = null;

        var jwtClaim = context.User.FindFirst("tenant_id");
        if (jwtClaim is not null && Guid.TryParse(jwtClaim.Value, out var claimTenantId))
            tenant = await tenantRepo.GetByIdAsync(claimTenantId);

        if (tenant is not null && context.Request.Headers.TryGetValue("X-Tenant-Id", out var requestedTenantId))
        {
            if (Guid.TryParse(requestedTenantId, out var headerTenantId) && headerTenantId != tenant.Id)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Requested tenant does not match the authenticated user's tenant." });
                return;
            }
        }

        if (tenant is null)
        {
            var host = context.Request.Host.Host;
            var parts = host.Split('.');
            if (parts.Length >= 3)
                tenant = await tenantRepo.GetBySlugAsync(parts[0]);
        }

        if (tenant is not null && context.User.FindFirst("tenant_slug") is { Value: var claimTenantSlug }
            && !string.IsNullOrWhiteSpace(claimTenantSlug)
            && !string.Equals(claimTenantSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Requested tenant slug does not match the authenticated user's tenant." });
            return;
        }

        if (tenant is null && context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue))
        {
            if (Guid.TryParse(headerValue, out var headerTenantId))
                tenant = await tenantRepo.GetByIdAsync(headerTenantId);
        }

        if (tenant is null)
        {
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

        if (!tenant.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant is inactive." });
            return;
        }

        var connectionString = tenantDbFactory.ResolveConnectionString(tenant);
        context.Items["TenantContext"] = new HttpTenantContext(tenant.Id, tenant.Slug, connectionString);

        await _next(context);
    }
}
