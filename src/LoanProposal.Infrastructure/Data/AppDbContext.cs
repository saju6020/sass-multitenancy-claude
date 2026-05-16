using LoanProposal.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LoanProposal.Infrastructure.Data;

/// <summary>
/// Tenant database context. Each resolved tenant uses its own physical database.
/// TenantId columns remain as audit metadata, but isolation is now provided by the database boundary.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

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
        modelBuilder.Ignore<Tenant>();

        modelBuilder.Entity<TenantConfiguration>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
            e.Ignore(c => c.Tenant);
        });

        modelBuilder.Entity<CustomField>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.TenantId, f.FieldKey }).IsUnique();
            e.Property(f => f.FieldKey).HasMaxLength(100).IsRequired();
            e.Property(f => f.Label).HasMaxLength(200).IsRequired();
            e.Ignore(f => f.Tenant);
        });

        modelBuilder.Entity<LoanProduct>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.TenantId, p.Code }).IsUnique();
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Code).HasMaxLength(50).IsRequired();
            e.Property(p => p.MinAmount).HasPrecision(18, 2);
            e.Property(p => p.MaxAmount).HasPrecision(18, 2);
            e.Ignore(p => p.Tenant);
            e.HasOne(p => p.WorkflowDefinition).WithMany().HasForeignKey(p => p.WorkflowDefinitionId);
        });

        modelBuilder.Entity<WorkflowDefinition>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.Name).HasMaxLength(200).IsRequired();
            e.Property(w => w.StepsJson).HasColumnType("jsonb");
            e.Property(w => w.RoutingRulesJson).HasColumnType("jsonb");
            e.Ignore(w => w.Tenant);
        });

        modelBuilder.Entity<RuleDefinition>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Expression).HasColumnType("jsonb");
            e.Ignore(r => r.Tenant);
        });

        modelBuilder.Entity<LoanApplication>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TenantId, a.ApplicationNumber }).IsUnique();
            e.Property(a => a.RequestedAmount).HasPrecision(18, 2);
            e.Property(a => a.ApplicationNumber).HasMaxLength(50).IsRequired();
            e.Property(a => a.CustomDataJson).HasColumnName("custom_data").HasColumnType("jsonb");
            e.Ignore(a => a.Tenant);
            e.HasOne(a => a.LoanProduct).WithMany().HasForeignKey(a => a.LoanProductId);
            e.HasOne(a => a.Applicant).WithMany().HasForeignKey(a => a.ApplicantId);
            e.HasOne(a => a.WorkflowDefinition).WithMany().HasForeignKey(a => a.WorkflowDefinitionId);
            e.HasMany(a => a.StateTransitions).WithOne(t => t.Application).HasForeignKey(t => t.ApplicationId);
            e.HasMany(a => a.Documents).WithOne(d => d.Application).HasForeignKey(d => d.ApplicationId);
        });

        modelBuilder.Entity<Applicant>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TenantId, a.NationalId }).IsUnique();
            e.Property(a => a.FullName).HasMaxLength(300).IsRequired();
            e.Property(a => a.Email).HasMaxLength(300);
            e.Property(a => a.AnnualIncome).HasPrecision(18, 2);
            e.Property(a => a.DebtToIncomeRatio).HasPrecision(5, 4);
            e.Ignore(a => a.Tenant);
        });

        modelBuilder.Entity<ApplicationStateTransition>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Action).HasMaxLength(100);
            e.Property(t => t.PerformedBy).HasMaxLength(300);
        });

        modelBuilder.Entity<ApplicationDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.FileName).HasMaxLength(500);
            e.Property(d => d.StorageKey).HasMaxLength(1000);
        });
    }
}