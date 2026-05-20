using LoanProposal.API.Services;
using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Data;
using LoanProposal.Infrastructure.Services;
using Shared.Contracts;

namespace LoanProposal.API.Middleware;

/// <summary>
/// Resolves the current tenant from claims/header/subdomain, then asks TenantRegistration
/// for the LoanProposal service database metadata.
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITenantRegistryClient tenantRegistryClient,
        TenantDbContextFactory tenantDbFactory)
    {
        TenantServiceDescriptor? tenant = null;

        var jwtClaim = context.User.FindFirst(AuthClaimTypes.TenantId);
        if (jwtClaim is not null && Guid.TryParse(jwtClaim.Value, out var claimTenantId))
            tenant = await tenantRegistryClient.GetLoanProposalTenantAsync(claimTenantId, context.RequestAborted);

        if (tenant is not null && context.Request.Headers.TryGetValue("X-Tenant-Id", out var requestedTenantId))
        {
            if (Guid.TryParse(requestedTenantId, out var headerTenantId) && headerTenantId != tenant.TenantId)
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
                tenant = await tenantRegistryClient.GetLoanProposalTenantBySlugAsync(parts[0], context.RequestAborted);
        }

        if (tenant is not null && context.User.FindFirst(AuthClaimTypes.TenantSlug) is { Value: var claimTenantSlug }
            && !string.IsNullOrWhiteSpace(claimTenantSlug)
            && !string.Equals(claimTenantSlug, tenant.TenantSlug, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Requested tenant slug does not match the authenticated user's tenant." });
            return;
        }

        if (tenant is null && context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue))
        {
            if (Guid.TryParse(headerValue, out var headerTenantId))
                tenant = await tenantRegistryClient.GetLoanProposalTenantAsync(headerTenantId, context.RequestAborted);
        }

        if (tenant is null)
        {
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

        await tenantDbFactory.EnsureCreatedAsync(tenant.ConnectionString, context.RequestAborted);
        context.Items["TenantContext"] = new HttpTenantContext(tenant.TenantId, tenant.TenantSlug, tenant.ConnectionString);

        await _next(context);
    }
}
