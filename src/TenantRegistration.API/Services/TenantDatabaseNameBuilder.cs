using LoanProposal.Core.Entities;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TenantRegistration.API.Services;

public class TenantDatabaseNameBuilder
{
    private readonly string _loanProposalConnectionTemplate;

    public TenantDatabaseNameBuilder(IConfiguration configuration)
    {
        _loanProposalConnectionTemplate = configuration.GetConnectionString("LoanProposalTemplate")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("A platform connection string is required.");
    }

    public string BuildDatabaseName(string slug)
    {
        var normalized = new string(slug.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"loanproposal_{normalized}";
    }

    public string BuildConnectionString(string slug)
    {
        var builder = new NpgsqlConnectionStringBuilder(_loanProposalConnectionTemplate)
        {
            Database = BuildDatabaseName(slug)
        };
        return builder.ConnectionString;
    }

    public string ResolveConnectionString(Tenant tenant) =>
        string.IsNullOrWhiteSpace(tenant.DatabaseConnectionString)
            ? BuildConnectionString(tenant.Slug)
            : tenant.DatabaseConnectionString;
}
