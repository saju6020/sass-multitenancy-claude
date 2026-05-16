using LoanProposal.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LoanProposal.Infrastructure.Data;

public class TenantDbContextFactory
{
    private readonly string _platformConnectionString;

    public TenantDbContextFactory(IConfiguration configuration)
    {
        _platformConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
    }

    public string BuildDatabaseName(string slug)
    {
        var normalized = new string(slug.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"loanproposal_{normalized}";
    }

    public string BuildConnectionString(string slug)
    {
        var builder = new NpgsqlConnectionStringBuilder(_platformConnectionString)
        {
            Database = BuildDatabaseName(slug)
        };
        return builder.ConnectionString;
    }

    public string ResolveConnectionString(Tenant tenant) =>
        string.IsNullOrWhiteSpace(tenant.DatabaseConnectionString)
            ? BuildConnectionString(tenant.Slug)
            : tenant.DatabaseConnectionString;

    public AppDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly("LoanProposal.Infrastructure"))
            .Options;
        return new AppDbContext(options);
    }

    public AppDbContext CreateDbContext(Tenant tenant) => CreateDbContext(ResolveConnectionString(tenant));
}