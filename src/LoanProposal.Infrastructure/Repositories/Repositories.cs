using LoanProposal.Core.Entities;
using LoanProposal.Core.Enums;
using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LoanProposal.Infrastructure.Repositories;

public class LoanApplicationRepository : TenantScopedRepository<LoanApplication>, ILoanApplicationRepository
{
    public LoanApplicationRepository(AppDbContext db, ITenantContext ctx) : base(db, ctx) { }

    public async Task<LoanApplication?> GetByApplicationNumberAsync(string applicationNumber, CancellationToken ct = default)
        => await Db.LoanApplications
            .Include(a => a.LoanProduct)
            .Include(a => a.Applicant)
            .Include(a => a.StateTransitions)
            .FirstOrDefaultAsync(a => a.ApplicationNumber == applicationNumber, ct);

    public async Task<IReadOnlyList<LoanApplication>> GetByStatusAsync(LoanApplicationStatus status, CancellationToken ct = default)
        => await Db.LoanApplications
            .Where(a => a.Status == status)
            .Include(a => a.Applicant)
            .Include(a => a.LoanProduct)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LoanApplication>> GetByApplicantAsync(Guid applicantId, CancellationToken ct = default)
        => await Db.LoanApplications
            .Where(a => a.ApplicantId == applicantId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Queries the JSONB custom_data column by field key using PostgreSQL's JSONB operators.
    /// This enables reporting and search over tenant-defined custom fields.
    /// For production, consider GIN indexes on the custom_data column for performance.
    /// </summary>
    public async Task<IReadOnlyList<LoanApplication>> SearchByCustomFieldAsync(
        string fieldKey, string value, CancellationToken ct = default)
    {
        // Uses EF.Functions.JsonContains â€” PostgreSQL JSONB @> operator
        var jsonFilter = $"{{\"{fieldKey}\": \"{value}\"}}";
        return await Db.LoanApplications
            .Where(a => EF.Functions.JsonContains(a.CustomDataJson, jsonFilter))
            .ToListAsync(ct);
    }
}

public class LoanProductRepository : TenantScopedRepository<LoanProduct>, ILoanProductRepository
{
    public LoanProductRepository(AppDbContext db, ITenantContext ctx) : base(db, ctx) { }

    public async Task<LoanProduct?> GetActiveByIdAsync(Guid id, CancellationToken ct = default)
        => await Db.LoanProducts
            .Include(p => p.WorkflowDefinition)
            .FirstOrDefaultAsync(p => p.Id == id && p.IsActive, ct);
}

public class WorkflowDefinitionRepository : TenantScopedRepository<WorkflowDefinition>, IWorkflowDefinitionRepository
{
    public WorkflowDefinitionRepository(AppDbContext db, ITenantContext ctx) : base(db, ctx) { }

    public async Task<WorkflowDefinition?> GetActiveVersionAsync(Guid workflowId, CancellationToken ct = default)
        => await Db.WorkflowDefinitions
            .Where(w => w.Id == workflowId && w.IsActive)
            .OrderByDescending(w => w.Version)
            .FirstOrDefaultAsync(ct);

    public async Task<WorkflowDefinition?> GetVersionAsync(Guid workflowId, int version, CancellationToken ct = default)
        => await Db.WorkflowDefinitions
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.Version == version, ct);

    /// <summary>
    /// Returns the workflow version that governed applications submitted at a specific point in time.
    /// Critical for ensuring in-flight applications are evaluated against the rules active at submission.
    /// </summary>
    public async Task<WorkflowDefinition?> GetVersionActiveAtAsync(Guid workflowId, DateTime pointInTime, CancellationToken ct = default)
        => await Db.WorkflowDefinitions
            .Where(w => w.Id == workflowId
                     && w.IsActive
                     && w.EffectiveFrom <= pointInTime
                     && (w.EffectiveTo == null || w.EffectiveTo >= pointInTime))
            .OrderByDescending(w => w.Version)
            .FirstOrDefaultAsync(ct);
}

public class RuleDefinitionRepository : TenantScopedRepository<RuleDefinition>, IRuleDefinitionRepository
{
    public RuleDefinitionRepository(AppDbContext db, ITenantContext ctx) : base(db, ctx) { }

    public async Task<IReadOnlyList<RuleDefinition>> GetByCategoryAsync(RuleCategory category, CancellationToken ct = default)
        => await Db.RuleDefinitions
            .Where(r => r.Category == category)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RuleDefinition>> GetApplicableToProductAsync(Guid productId, CancellationToken ct = default)
    {
        var all = await Db.RuleDefinitions.ToListAsync(ct);
        // Rules with null ProductScope apply to all products
        return all.Where(r =>
            r.GetProductScope() is null ||
            r.GetProductScope()!.Contains(productId))
            .OrderBy(r => r.Priority)
            .ToList();
    }
}

public class CustomFieldRepository : TenantScopedRepository<CustomField>, ICustomFieldRepository
{
    public CustomFieldRepository(AppDbContext db, ITenantContext ctx) : base(db, ctx) { }

    public async Task<CustomField?> GetByFieldKeyAsync(string fieldKey, CancellationToken ct = default)
        => await Db.CustomFields.FirstOrDefaultAsync(f => f.FieldKey == fieldKey, ct);

    public async Task<IReadOnlyList<CustomField>> GetSearchableFieldsAsync(CancellationToken ct = default)
        => await Db.CustomFields.Where(f => f.IsSearchable).ToListAsync(ct);
}

public class TenantRepository : IRepository<Tenant>, ITenantRepository
{
    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbFactory;

    public TenantRepository(PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory)
    {
        _platformDb = platformDb;
        _tenantDbFactory = tenantDbFactory;
    }

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct = default) =>
        await _platformDb.Tenants.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);

    public async Task AddAsync(Tenant entity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entity.DatabaseConnectionString))
            entity.ConfigureDatabase(_tenantDbFactory.BuildDatabaseName(entity.Slug), _tenantDbFactory.BuildConnectionString(entity.Slug));

        await _platformDb.Tenants.AddAsync(entity, ct);
        await using var tenantDb = _tenantDbFactory.CreateDbContext(entity);
        await tenantDb.Database.EnsureCreatedAsync(ct);
    }

    public Task UpdateAsync(Tenant entity, CancellationToken ct = default)
    {
        _platformDb.Tenants.Update(entity);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _platformDb.SaveChangesAsync(ct);

    public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        _platformDb.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);

    public async Task<TenantConfiguration?> GetConfigAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        var tenant = await GetByIdAsync(tenantId, ct);
        if (tenant is null) return null;
        await using var tenantDb = _tenantDbFactory.CreateDbContext(tenant);
        return await tenantDb.TenantConfigurations.FirstOrDefaultAsync(c => c.Key == key, ct);
    }

    public async Task SetConfigAsync(TenantConfiguration config, CancellationToken ct = default)
    {
        var tenant = await GetByIdAsync(config.TenantId, ct)
            ?? throw new InvalidOperationException("Tenant was not found.");
        await using var tenantDb = _tenantDbFactory.CreateDbContext(tenant);
        var existing = await tenantDb.TenantConfigurations.FirstOrDefaultAsync(c => c.Key == config.Key, ct);
        if (existing is null) tenantDb.TenantConfigurations.Add(config);
        else tenantDb.TenantConfigurations.Update(config);
        await tenantDb.SaveChangesAsync(ct);
    }
}