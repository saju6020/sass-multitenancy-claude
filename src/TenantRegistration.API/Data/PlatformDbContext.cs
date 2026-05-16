using LoanProposal.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace TenantRegistration.API.Data;

public class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.Property(t => t.Slug).HasMaxLength(100).IsRequired();
            e.Property(t => t.DefaultCurrency).HasMaxLength(3);
            e.Property(t => t.DatabaseName).HasMaxLength(200);
            e.Property(t => t.DatabaseConnectionString).HasMaxLength(2000);
            e.Ignore(t => t.Configurations);
            e.Ignore(t => t.LoanProducts);
            e.Ignore(t => t.CustomFields);
            e.Ignore(t => t.WorkflowDefinitions);
        });

        modelBuilder.Entity<PlatformUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.TenantId);
            e.Property(u => u.TenantSlug).HasMaxLength(100);
            e.Property(u => u.Email).HasMaxLength(300).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(300).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(1000).IsRequired();
            e.Property(u => u.RolesCsv).HasMaxLength(500).IsRequired();
        });
    }
}
