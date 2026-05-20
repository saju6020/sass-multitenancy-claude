using LoanProposal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace TenantRegistration.API.Services;

public class LoanProposalTenantDatabaseProvisioner
{
    public async Task EnsureCreatedAsync(string connectionString, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly("LoanProposal.Infrastructure"))
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
    }
}
