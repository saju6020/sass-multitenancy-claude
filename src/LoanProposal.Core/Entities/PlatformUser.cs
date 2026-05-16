namespace LoanProposal.Core.Entities;

/// <summary>
/// Platform-owned login user. Users live in the platform database because
/// authentication happens before a tenant database can be selected.
/// </summary>
public class PlatformUser
{
    public Guid Id { get; private set; }
    public Guid? TenantId { get; private set; }
    public string TenantSlug { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string RolesCsv { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    private PlatformUser() { }

    public static PlatformUser Create(Guid? tenantId, string tenantSlug, string email, string fullName, IEnumerable<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        return new PlatformUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TenantSlug = tenantSlug,
            Email = email.Trim().ToLowerInvariant(),
            FullName = fullName.Trim(),
            RolesCsv = string.Join(",", roles.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct()),
            CreatedAt = DateTime.UtcNow
        };
    }

    public IReadOnlyList<string> GetRoles() =>
        RolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }
}
