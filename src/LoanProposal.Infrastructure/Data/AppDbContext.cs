using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace LoanProposal.Infrastructure.Data;

/// <summary>
/// Main EF Core DbContext with automatic tenant scoping via global query filters.
/// Every query on tenant-scoped entities is automatically filtered by TenantId —
/// ensuring strict data isolation without calling code needing to remember to filter.
/// </summary>
public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    // ── DbSets ──────────────────────────────────────────────────────────────
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantConfiguration> TenantConfigurations => Set<TenantConfiguration>();
    public DbSet<LoanProduct> LoanProducts => Set<LoanProduct>();
    public DbSet<CustomField> CustomFields => Set<CustomField>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<RuleDefinition> RuleDefinitions => Set<RuleDefinition>();
    public DbSet<LoanApplication> LoanApplications => Set<LoanApplication>();
    public DbSet<Applicant> Applicants => Set<Applicant>();
    public DbSet<ApplicationStateTransition> StateTransitions => Set<ApplicationStateTransition>();
    public DbSet<ApplicationDocument> ApplicationDocuments => Set<ApplicationDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Tenant (no filter — platform-level entity) ──────────────────
        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.Property(t => t.Slug).HasMaxLength(100).IsRequired();
            e.Property(t => t.DefaultCurrency).HasMaxLength(3);
        });

        // ── TenantConfiguration ─────────────────────────────────────────
        modelBuilder.Entity<TenantConfiguration>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
            e.HasQueryFilter(c => c.TenantId == _tenantContext.TenantId);
            e.HasOne(c => c.Tenant).WithMany(t => t.Configurations).HasForeignKey(c => c.TenantId);
        });

        // ── CustomField — with global tenant filter ─────────────────────
        modelBuilder.Entity<CustomField>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.TenantId, f.FieldKey }).IsUnique();
            e.HasQueryFilter(f => f.TenantId == _tenantContext.TenantId && f.IsActive);
            e.Property(f => f.FieldKey).HasMaxLength(100).IsRequired();
            e.Property(f => f.Label).HasMaxLength(200).IsRequired();
            e.HasOne(f => f.Tenant).WithMany(t => t.CustomFields).HasForeignKey(f => f.TenantId);
        });

        // ── LoanProduct ─────────────────────────────────────────────────
        modelBuilder.Entity<LoanProduct>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.TenantId, p.Code }).IsUnique();
            e.HasQueryFilter(p => p.TenantId == _tenantContext.TenantId);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Code).HasMaxLength(50).IsRequired();
            e.Property(p => p.MinAmount).HasPrecision(18, 2);
            e.Property(p => p.MaxAmount).HasPrecision(18, 2);
            e.HasOne(p => p.Tenant).WithMany(t => t.LoanProducts).HasForeignKey(p => p.TenantId);
            e.HasOne(p => p.WorkflowDefinition).WithMany().HasForeignKey(p => p.WorkflowDefinitionId);
        });

        // ── WorkflowDefinition ──────────────────────────────────────────
        modelBuilder.Entity<WorkflowDefinition>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasQueryFilter(w => w.TenantId == _tenantContext.TenantId);
            e.Property(w => w.Name).HasMaxLength(200).IsRequired();
            e.Property(w => w.StepsJson).HasColumnType("jsonb");
            e.Property(w => w.RoutingRulesJson).HasColumnType("jsonb");
            e.HasOne(w => w.Tenant).WithMany(t => t.WorkflowDefinitions).HasForeignKey(w => w.TenantId);
        });

        // ── RuleDefinition ──────────────────────────────────────────────
        modelBuilder.Entity<RuleDefinition>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasQueryFilter(r => r.TenantId == _tenantContext.TenantId && r.IsActive);
            e.Property(r => r.Expression).HasColumnType("jsonb");
            e.HasOne(r => r.Tenant).WithMany().HasForeignKey(r => r.TenantId);
        });

        // ── LoanApplication ─────────────────────────────────────────────
        modelBuilder.Entity<LoanApplication>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TenantId, a.ApplicationNumber }).IsUnique();
            e.HasQueryFilter(a => a.TenantId == _tenantContext.TenantId);
            e.Property(a => a.RequestedAmount).HasPrecision(18, 2);
            e.Property(a => a.ApplicationNumber).HasMaxLength(50).IsRequired();

            // JSONB column for custom + extended fields — flexible but indexed in PostgreSQL
            e.Property(a => a.CustomDataJson).HasColumnName("custom_data").HasColumnType("jsonb");

            e.HasOne(a => a.Tenant).WithMany().HasForeignKey(a => a.TenantId);
            e.HasOne(a => a.LoanProduct).WithMany().HasForeignKey(a => a.LoanProductId);
            e.HasOne(a => a.Applicant).WithMany().HasForeignKey(a => a.ApplicantId);
            e.HasOne(a => a.WorkflowDefinition).WithMany().HasForeignKey(a => a.WorkflowDefinitionId);
            e.HasMany(a => a.StateTransitions).WithOne(t => t.Application).HasForeignKey(t => t.ApplicationId);
            e.HasMany(a => a.Documents).WithOne(d => d.Application).HasForeignKey(d => d.ApplicationId);
        });

        // ── Applicant ───────────────────────────────────────────────────
        modelBuilder.Entity<Applicant>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TenantId, a.NationalId }).IsUnique();
            e.HasQueryFilter(a => a.TenantId == _tenantContext.TenantId);
            e.Property(a => a.FullName).HasMaxLength(300).IsRequired();
            e.Property(a => a.Email).HasMaxLength(300);
            e.Property(a => a.AnnualIncome).HasPrecision(18, 2);
            e.Property(a => a.DebtToIncomeRatio).HasPrecision(5, 4);
            e.HasOne(a => a.Tenant).WithMany().HasForeignKey(a => a.TenantId);
        });

        // ── ApplicationStateTransition — immutable audit log ────────────
        modelBuilder.Entity<ApplicationStateTransition>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasQueryFilter(t => t.TenantId == _tenantContext.TenantId);
            e.Property(t => t.Action).HasMaxLength(100);
            e.Property(t => t.PerformedBy).HasMaxLength(300);
        });

        // ── ApplicationDocument ─────────────────────────────────────────
        modelBuilder.Entity<ApplicationDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasQueryFilter(d => d.TenantId == _tenantContext.TenantId);
            e.Property(d => d.FileName).HasMaxLength(500);
            e.Property(d => d.StorageKey).HasMaxLength(1000);
        });
    }
}
