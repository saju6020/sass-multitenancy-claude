namespace Shared.Contracts;

public record TenantServiceDescriptor(
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    string Currency,
    string Timezone,
    string ServiceName,
    string DatabaseName,
    string ConnectionString,
    bool IsActive);

public record TenantOptionDto(Guid Id, string Name, string Slug, string Currency);

public record TokenRequestDto(string Email, string Password);

public record TokenResponseDto(
    string Access_Token,
    string Token_Type,
    DateTime Expires_At,
    Guid? Tenant_Id,
    string Tenant_Slug,
    IReadOnlyList<string> Roles);

public record CreateTenantDto(string Name, string Slug, string Currency, string Timezone);
