using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LoanProposal.Infrastructure.Repositories;

/// <summary>
/// Base repository for data stored inside the resolved tenant database.
/// Physical database selection is handled before this repository is created.
/// </summary>
public abstract class TenantScopedRepository<T> : ITenantScopedRepository<T> where T : class
{
    protected readonly AppDbContext Db;
    protected readonly ITenantContext TenantContext;

    protected TenantScopedRepository(AppDbContext db, ITenantContext tenantContext)
    {
        Db = db;
        TenantContext = tenantContext;
    }

    public virtual async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await Db.Set<T>().FindAsync([id], ct);

    public virtual async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
        => await Db.Set<T>().ToListAsync(ct);

    public virtual async Task AddAsync(T entity, CancellationToken ct = default)
        => await Db.Set<T>().AddAsync(entity, ct);

    public virtual Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        Db.Set<T>().Update(entity);
        return Task.CompletedTask;
    }

    public virtual async Task SaveChangesAsync(CancellationToken ct = default)
        => await Db.SaveChangesAsync(ct);
}