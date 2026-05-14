using LoanProposal.API.Middleware;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Data;
using LoanProposal.Infrastructure.Repositories;
using LoanProposal.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

// ── Database ─────────────────────────────────────────────────────────────────
// PostgreSQL chosen for JSONB support (custom field storage + querying)
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.MigrationsAssembly("LoanProposal.Infrastructure")
    );

    // Register tenant context factory so EF global query filters can access it
    // The ITenantContext is request-scoped; AppDbContext reads it per-query.
});

// ── Tenant Resolution ─────────────────────────────────────────────────────────
// ITenantContext is Scoped — one instance per HTTP request.
// It's populated by TenantResolutionMiddleware BEFORE any service uses it.
builder.Services.AddScoped<ITenantContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    if (httpContext?.Items["TenantContext"] is ITenantContext ctx)
        return ctx;

    // Background jobs or platform routes: return a platform context
    return new PlatformTenantContext();
});
builder.Services.AddHttpContextAccessor();

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<ILoanApplicationRepository, LoanApplicationRepository>();
builder.Services.AddScoped<ILoanProductRepository, LoanProductRepository>();
builder.Services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
builder.Services.AddScoped<IRuleDefinitionRepository, RuleDefinitionRepository>();
builder.Services.AddScoped<ICustomFieldRepository, CustomFieldRepository>();
// ITenantRepository uses a platform-context repo (not tenant-scoped) since it manages all tenants
builder.Services.AddScoped<ITenantRepository, TenantRepository>(); // defined below

// ── Domain Services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<IRuleEngine, JsonLogicRuleEngine>();
builder.Services.AddScoped<INotificationService, LoggingNotificationService>();
builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
builder.Services.AddScoped<SlaTimerService>();

// ── MediatR (CQRS) ────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(LoanProposal.Application.Commands.SubmitLoanApplicationCommand).Assembly));

// ── Authentication & Authorization ────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            // In production: IssuerSigningKey from key vault, not config file
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TenantAdmin", policy => policy.RequireRole("TenantAdmin", "PlatformAdmin"));
    options.AddPolicy("PlatformAdmin", policy => policy.RequireRole("PlatformAdmin"));
    options.AddPolicy("LoanOfficer", policy => policy.RequireRole("LoanOfficer", "TenantAdmin", "PlatformAdmin"));
});

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LoanProposal SaaS API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "JWT with tenant_id claim"
    });
});

// ── CORS — per-tenant domain whitelisting would go here ──────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── Middleware Pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Tenant resolution runs AFTER authentication so JWT claims are available.
// MVC screens use platform-level read models and tenant selectors, so only
// API/platform endpoints require request tenant resolution.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.StartsWithSegments("/platform"),
    branch => branch.UseMiddleware<TenantResolutionMiddleware>());

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// ── Database Migration on Startup (dev only) ──────────────────────────────────
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    try
    {
        // Note: migrations use PlatformTenantContext (TenantId = Guid.Empty)
        // so global query filters are effectively disabled during migration
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrations = db.Database.GetMigrations();
        if (migrations.Any())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
            if (!await TableExistsAsync(db, "Tenants"))
            {
                var creator = db.GetService<IRelationalDatabaseCreator>();
                await creator.CreateTablesAsync();
            }
        }

        await SeedDemoData(scope.ServiceProvider);
        await EnsureAcmeWorkflowAsync(scope.ServiceProvider);
        await EnsureGlobalFinanceWorkflowAsync(scope.ServiceProvider);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(
            ex,
            "Skipping development database migration/seed. Configure ConnectionStrings:DefaultConnection to enable persistence.");
    }
}

app.Run();

// ── Demo Seed Data ────────────────────────────────────────────────────────────
static async Task<bool> TableExistsAsync(AppDbContext db, string tableName)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State == System.Data.ConnectionState.Closed;

    if (shouldClose)
    {
        await connection.OpenAsync();
    }

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tableName";
        parameter.Value = $"\"{tableName}\"";
        command.Parameters.Add(parameter);

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
    finally
    {
        if (shouldClose)
        {
            await connection.CloseAsync();
        }
    }
}

static async Task SeedDemoData(IServiceProvider services)
{
    var db = services.GetRequiredService<AppDbContext>();
    if (await db.Tenants.AnyAsync()) return;

    Console.WriteLine("[Seed] Creating demo tenants...");

    // Tenant 1: Standard bank with a two-stage approval workflow
    var acmeBank = Tenant.Create("Acme Bank", "acme-bank", "USD", "America/New_York");
    var globalFinance = Tenant.Create("Global Finance MFI", "global-finance", "BDT", "Asia/Dhaka");
    var islamicBank = Tenant.Create("Al-Baraka Islamic Finance", "al-baraka", "SAR", "Asia/Riyadh");

    await db.Tenants.AddRangeAsync(acmeBank, globalFinance, islamicBank);

    // Tenant-specific configurations
    await db.TenantConfigurations.AddRangeAsync(
        TenantConfiguration.Create(acmeBank.Id, TenantConfigKeys.FastTrackMaxAmount, "50000", ConfigValueType.Decimal, "seed"),
        TenantConfiguration.Create(acmeBank.Id, TenantConfigKeys.SlaFastTrackHours, "24", ConfigValueType.Integer, "seed"),
        TenantConfiguration.Create(acmeBank.Id, TenantConfigKeys.SlaStandardHours, "120", ConfigValueType.Integer, "seed"),
        TenantConfiguration.Create(acmeBank.Id, TenantConfigKeys.CreditScoreThreshold, "620", ConfigValueType.Integer, "seed"),
        TenantConfiguration.Create(acmeBank.Id, TenantConfigKeys.DtiRatioMax, "0.45", ConfigValueType.Decimal, "seed"),
        TenantConfiguration.Create(acmeBank.Id, TenantConfigKeys.RelationshipYearsForBypass, "3", ConfigValueType.Integer, "seed"),

        // Middle East tenant: working days are Sun-Thu
        TenantConfiguration.Create(islamicBank.Id, TenantConfigKeys.BusinessCalendar,
            "[0,1,2,3,4]", ConfigValueType.Json, "seed",  // Sun=0, Mon=1, Tue=2, Wed=3, Thu=4
            "Working days: Sunday to Thursday"),
        TenantConfiguration.Create(islamicBank.Id, TenantConfigKeys.DtiRatioMax, "0.55", ConfigValueType.Decimal, "seed",
            "Higher DTI allowed for government-backed products")
    );

    // Acme Bank custom fields
    await db.CustomFields.AddRangeAsync(
        CustomField.Create(acmeBank.Id, "origination_channel", "Origination Channel",
            CustomFieldType.Select, isRequired: true, isSearchable: true),
        CustomField.Create(acmeBank.Id, "relationship_manager_id", "Relationship Manager",
            CustomFieldType.Text, isRequired: false)
    );

    // Global Finance (MFI) custom fields for microfinance
    var gstField = CustomField.Create(globalFinance.Id, "gst_registration_number", "GST Registration Number",
        CustomFieldType.Text, isRequired: false, isSearchable: true);
    var gstRegistered = CustomField.Create(globalFinance.Id, "gst_registered", "GST Registered?",
        CustomFieldType.Boolean, isRequired: true);
    var dependentsField = CustomField.Create(globalFinance.Id, "number_of_dependents", "Number of Dependents",
        CustomFieldType.Number, isRequired: true);

    await db.CustomFields.AddRangeAsync(gstField, gstRegistered, dependentsField);

    // Eligibility rule: DTI <= 45% AND credit score >= 620 → eligible
    var eligibilityRule = RuleDefinition.Create(
        acmeBank.Id,
        "Standard Eligibility Check",
        RuleCategory.Eligibility,
        expression: """
        {
          "and": [
            {">=": [{"var": "applicant.creditScore"}, 620]},
            {"<=": [{"var": "applicant.dtiRatio"}, 0.45]}
          ]
        }
        """,
        outcome: RuleOutcome.FlagForReview,
        createdBy: "seed"
    );
    await db.RuleDefinitions.AddAsync(eligibilityRule);

    // GST-registered → max loan amount increases by 20% rule
    var gstAmountRule = RuleDefinition.Create(
        globalFinance.Id,
        "GST Registration Bonus — +20% Max Amount",
        RuleCategory.AmountAdjust,
        expression: """{"==": [{"var": "custom.gst_registered"}, true]}""",
        outcome: RuleOutcome.AdjustMaxAmount,
        createdBy: "seed"
    );
    gstAmountRule.GetType().GetProperty("OutcomeData")!
        .SetValue(gstAmountRule, "1.20");  // 20% multiplier
    await db.RuleDefinitions.AddAsync(gstAmountRule);

    await db.SaveChangesAsync();
    Console.WriteLine("[Seed] Done — created 3 demo tenants with configurations, custom fields, and rules.");
}

// ── Stub TenantRepository for compilation ─────────────────────────────────────
// In a real project, this lives in Infrastructure/Repositories/TenantRepository.cs
static async Task EnsureAcmeWorkflowAsync(IServiceProvider services)
{
    var db = services.GetRequiredService<AppDbContext>();
    var acmeBank = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == "acme-bank");
    if (acmeBank is null) return;

    var workflow = await db.WorkflowDefinitions
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(w => w.TenantId == acmeBank.Id && w.Name == "Acme Simple Approval");

    if (workflow is null)
    {
        workflow = WorkflowDefinition.Create(acmeBank.Id, "Acme Simple Approval", "seed", DateTime.UtcNow);
        workflow.SetSteps([
            new WorkflowStepDefinition
            {
                StepId = "data_entry",
                Name = "Data Entry",
                StepType = WorkflowStepType.DataEntry,
                AssigneeRoleCode = "LoanOfficer",
                NextStepIds = ["branch_review"]
            },
            new WorkflowStepDefinition
            {
                StepId = "branch_review",
                Name = "Branch Review",
                StepType = WorkflowStepType.Approval,
                AssigneeRoleCode = "BranchManager",
                SlaHours = 24,
                NextStepIds = ["approved", "rejected"]
            },
            new WorkflowStepDefinition
            {
                StepId = "approved",
                Name = "Approved",
                StepType = WorkflowStepType.Terminal,
                NextStepIds = []
            },
            new WorkflowStepDefinition
            {
                StepId = "rejected",
                Name = "Rejected",
                StepType = WorkflowStepType.Terminal,
                NextStepIds = []
            }
        ]);
        workflow.SetRoutingRules([
            new RoutingRule
            {
                FromStepId = "data_entry",
                ToStepId = "branch_review",
                Priority = 1,
                Description = "Completed proposals go to branch review."
            },
            new RoutingRule
            {
                FromStepId = "branch_review",
                ToStepId = "approved",
                Priority = 1,
                Description = "Simple approval path."
            }
        ]);
        workflow.Activate(DateTime.UtcNow);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
    }

    var productExists = await db.LoanProducts
        .IgnoreQueryFilters()
        .AnyAsync(p => p.TenantId == acmeBank.Id && p.Code == "ACME-SIMPLE");

    if (!productExists)
    {
        db.LoanProducts.Add(LoanProduct.Create(
            acmeBank.Id,
            "Acme Simple Loan",
            "ACME-SIMPLE",
            LoanProductType.Personal,
            1000,
            100000,
            workflow.Id));
        await db.SaveChangesAsync();
    }
}

static async Task EnsureGlobalFinanceWorkflowAsync(IServiceProvider services)
{
    var db = services.GetRequiredService<AppDbContext>();
    var globalFinance = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == "global-finance");
    if (globalFinance is null) return;

    var workflow = await db.WorkflowDefinitions
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(w => w.TenantId == globalFinance.Id && w.Name == "Global Finance Microfinance Approval");

    if (workflow is null)
    {
        workflow = WorkflowDefinition.Create(globalFinance.Id, "Global Finance Microfinance Approval", "seed", DateTime.UtcNow);
        workflow.SetSteps([
            new WorkflowStepDefinition
            {
                StepId = "field_officer_intake",
                Name = "Field Officer Intake",
                StepType = WorkflowStepType.DataEntry,
                AssigneeRoleCode = "FieldOfficer",
                NextStepIds = ["gst_screening"]
            },
            new WorkflowStepDefinition
            {
                StepId = "gst_screening",
                Name = "GST Screening",
                StepType = WorkflowStepType.AutomatedCheck,
                SlaHours = 4,
                NextStepIds = ["branch_credit_review", "group_lead_review"]
            },
            new WorkflowStepDefinition
            {
                StepId = "branch_credit_review",
                Name = "Branch Credit Review",
                StepType = WorkflowStepType.Approval,
                AssigneeRoleCode = "BranchCreditOfficer",
                SlaHours = 24,
                NextStepIds = ["approved", "rejected"]
            },
            new WorkflowStepDefinition
            {
                StepId = "group_lead_review",
                Name = "Group Lead Review",
                StepType = WorkflowStepType.Approval,
                AssigneeRoleCode = "GroupLead",
                SlaHours = 12,
                NextStepIds = ["approved", "rejected"]
            },
            new WorkflowStepDefinition
            {
                StepId = "approved",
                Name = "Approved",
                StepType = WorkflowStepType.Terminal,
                NextStepIds = []
            },
            new WorkflowStepDefinition
            {
                StepId = "rejected",
                Name = "Rejected",
                StepType = WorkflowStepType.Terminal,
                NextStepIds = []
            }
        ]);
        workflow.SetRoutingRules([
            new RoutingRule
            {
                FromStepId = "field_officer_intake",
                ToStepId = "gst_screening",
                Priority = 1,
                Description = "All microfinance proposals receive automated GST screening."
            },
            new RoutingRule
            {
                FromStepId = "gst_screening",
                ToStepId = "branch_credit_review",
                ConditionExpression = "CustomField['gst_registered'] == true",
                Priority = 1,
                Description = "GST-registered borrowers move to branch credit review."
            },
            new RoutingRule
            {
                FromStepId = "gst_screening",
                ToStepId = "group_lead_review",
                Priority = 2,
                Description = "Non-GST borrowers receive group lead review."
            },
            new RoutingRule
            {
                FromStepId = "branch_credit_review",
                ToStepId = "approved",
                Priority = 1,
                Description = "Simple approval path after branch credit review."
            },
            new RoutingRule
            {
                FromStepId = "group_lead_review",
                ToStepId = "approved",
                Priority = 1,
                Description = "Simple approval path after group lead review."
            }
        ]);
        workflow.Activate(DateTime.UtcNow);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
    }

    var productExists = await db.LoanProducts
        .IgnoreQueryFilters()
        .AnyAsync(p => p.TenantId == globalFinance.Id && p.Code == "GLOBAL-MICRO");

    if (!productExists)
    {
        db.LoanProducts.Add(LoanProduct.Create(
            globalFinance.Id,
            "Global Micro Enterprise Loan",
            "GLOBAL-MICRO",
            LoanProductType.Microfinance,
            5000,
            250000,
            workflow.Id));
        await db.SaveChangesAsync();
    }
}

public class TenantRepository : ITenantRepository
{
    private readonly AppDbContext _db;
    public TenantRepository(AppDbContext db) => _db = db;

    public Task<LoanProposal.Core.Entities.Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Tenants.FindAsync([id], ct).AsTask()!;

    public Task<IReadOnlyList<LoanProposal.Core.Entities.Tenant>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoanProposal.Core.Entities.Tenant>>(
            _db.Tenants.Where(t => t.IsActive).ToList());

    public Task AddAsync(LoanProposal.Core.Entities.Tenant entity, CancellationToken ct = default)
    {
        _db.Tenants.Add(entity);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(LoanProposal.Core.Entities.Tenant entity, CancellationToken ct = default)
    {
        _db.Tenants.Update(entity);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    public async Task<LoanProposal.Core.Entities.Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);

    public async Task<TenantConfiguration?> GetConfigAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        await _db.TenantConfigurations.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Key == key, ct);

    public async Task SetConfigAsync(TenantConfiguration config, CancellationToken ct = default)
    {
        var existing = await GetConfigAsync(config.TenantId, config.Key, ct);
        if (existing is null) _db.TenantConfigurations.Add(config);
        else _db.TenantConfigurations.Update(config);
    }
}
