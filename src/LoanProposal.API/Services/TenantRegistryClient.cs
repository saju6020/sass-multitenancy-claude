using System.Net.Http.Json;
using Shared.Contracts;

namespace LoanProposal.API.Services;

public interface ITenantRegistryClient
{
    Task<IReadOnlyList<TenantOptionDto>> GetTenantsAsync(CancellationToken ct = default);
    Task<TenantServiceDescriptor?> GetLoanProposalTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<TenantServiceDescriptor?> GetLoanProposalTenantBySlugAsync(string slug, CancellationToken ct = default);
}

public class TenantRegistryClient : ITenantRegistryClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public TenantRegistryClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<TenantOptionDto>> GetTenantsAsync(CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Get, "/internal/tenants");
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<TenantOptionDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<TenantServiceDescriptor?> GetLoanProposalTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Get, $"/internal/tenants/{tenantId}/services/loan-proposal");
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TenantServiceDescriptor>(cancellationToken: ct);
    }

    public async Task<TenantServiceDescriptor?> GetLoanProposalTenantBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Get, $"/internal/tenants/slug/{Uri.EscapeDataString(slug)}/services/loan-proposal");
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TenantServiceDescriptor>(cancellationToken: ct);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Internal-Api-Key", _configuration["TenantRegistry:InternalApiKey"] ?? "dev-internal-registry-key");
        return request;
    }
}
