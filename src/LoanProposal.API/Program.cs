using Elsa.Extensions;
using LoanProposal.API.Middleware;
using LoanProposal.API.Controllers;
using LoanProposal.Application.Commands;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;
using LoanProposal.Infrastructure.Data;
using LoanProposal.Infrastructure.Repositories;
using LoanProposal.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.MigrationsAssembly("LoanProposal.Infrastructure")));

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var tenantContext = serviceProvider.GetRequiredService<ITenantContext>();
    if (string.IsNullOrWhiteSpace(tenantContext.ConnectionString))
        throw new InvalidOperationException("Tenant database connection string was not resolved for this request.");

    options.UseNpgsql(
        tenantContext.ConnectionString,
        npgsql => npgsql.MigrationsAssembly("LoanProposal.Infrastructure"));
});

builder.Services.AddSingleton<TenantDbContextFactory>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    if (httpContext?.Items["TenantContext"] is ITenantContext ctx)
        return ctx;

    return new PlatformTenantContext();
});

builder.Services.AddScoped<ILoanApplicationRepository, LoanApplicationRepository>();
builder.Services.AddScoped<ILoanProductRepository, LoanProductRepository>();
builder.Services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
builder.Services.AddScoped<IRuleDefinitionRepository, RuleDefinitionRepository>();
builder.Services.AddScoped<ICustomFieldRepository, CustomFieldRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();

builder.Services.AddScoped<IRuleEngine, JsonLogicRuleEngine>();
builder.Services.AddScoped<INotificationService, LoggingNotificationService>();
builder.Services.AddElsa();
builder.Services.AddScoped<IWorkflowEngine, ElsaWorkflowEngine>();
builder.Services.AddScoped<SlaTimerService>();
builder.Services.AddScoped<PasswordHasher<PlatformUser>>();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(SubmitLoanApplicationCommand).Assembly));

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Smart";
        options.DefaultChallengeScheme = "Smart";
    })
    .AddPolicyScheme("Smart", "Cookie or Bearer", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/access-denied";
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TokenController.GetSigningKey(builder.Configuration))),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("PlatformAdmin", policy => policy.RequireRole("PlatformAdmin"));
    options.AddPolicy("TenantAdmin", policy => policy.RequireRole("TenantAdmin", "PlatformAdmin"));
    options.AddPolicy("LoanOfficer", policy => policy.RequireRole("LoanOfficer", "TenantAdmin", "PlatformAdmin"));
    options.AddPolicy("LoanReviewer", policy => policy.RequireRole("Reviewer", "TenantAdmin", "PlatformAdmin"));
    options.AddPolicy("LoanApprover", policy => policy.RequireRole("Approver", "TenantAdmin", "PlatformAdmin"));
    options.AddPolicy("LoanParticipant", policy => policy.RequireRole("LoanOfficer", "Reviewer", "Approver", "TenantAdmin", "PlatformAdmin"));
});

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

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

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

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.StartsWithSegments("/platform"),
    branch => branch.UseMiddleware<TenantResolutionMiddleware>());

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    try
    {
        var platformDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await platformDb.Database.EnsureCreatedAsync();
        await EnsurePlatformTenantDatabaseColumnsAsync(platformDb);

        var tenantDbFactory = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<PlatformUser>>();
        await SeedPlatformTenantsAsync(platformDb, tenantDbFactory);
        await SeedPlatformUsersAsync(platformDb, passwordHasher);
        await SeedTenantDatabasesAsync(platformDb, tenantDbFactory);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Skipping development database provisioning/seed. Configure ConnectionStrings:DefaultConnection to enable persistence.");
    }
}

app.Run();


static async Task EnsurePlatformTenantDatabaseColumnsAsync(PlatformDbContext platformDb)
{
    await platformDb.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Tenants" ADD COLUMN IF NOT EXISTS "DatabaseName" character varying(200) NOT NULL DEFAULT '';
        ALTER TABLE "Tenants" ADD COLUMN IF NOT EXISTS "DatabaseConnectionString" character varying(2000) NOT NULL DEFAULT '';
        CREATE TABLE IF NOT EXISTS "PlatformUsers" (
            "Id" uuid NOT NULL,
            "TenantId" uuid NULL,
            "TenantSlug" character varying(100) NOT NULL,
            "Email" character varying(300) NOT NULL,
            "FullName" character varying(300) NOT NULL,
            "PasswordHash" character varying(1000) NOT NULL,
            "RolesCsv" character varying(500) NOT NULL,
            "IsActive" boolean NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_PlatformUsers" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlatformUsers_Email" ON "PlatformUsers" ("Email");
        CREATE INDEX IF NOT EXISTS "IX_PlatformUsers_TenantId" ON "PlatformUsers" ("TenantId");
        """);
}

static async Task SeedPlatformUsersAsync(PlatformDbContext platformDb, PasswordHasher<PlatformUser> passwordHasher)
{
    var tenants = await platformDb.Tenants.OrderBy(t => t.Name).ToListAsync();

    await EnsureUserAsync(platformDb, passwordHasher, null, "__platform__", "platform@loanproposal.local", "Platform Administrator", ["PlatformAdmin"]);

    foreach (var tenant in tenants)
    {
        var prefix = tenant.Slug.Replace("-", "");
        await EnsureUserAsync(platformDb, passwordHasher, tenant.Id, tenant.Slug, $"admin@{tenant.Slug}.local", $"{tenant.Name} Admin", ["TenantAdmin"]);
        await EnsureUserAsync(platformDb, passwordHasher, tenant.Id, tenant.Slug, $"officer@{tenant.Slug}.local", $"{tenant.Name} Loan Officer", ["LoanOfficer"]);
        await EnsureUserAsync(platformDb, passwordHasher, tenant.Id, tenant.Slug, $"reviewer@{tenant.Slug}.local", $"{tenant.Name} Reviewer", ["Reviewer"]);
        await EnsureUserAsync(platformDb, passwordHasher, tenant.Id, tenant.Slug, $"approver@{tenant.Slug}.local", $"{tenant.Name} Approver", ["Approver"]);

        if (prefix != tenant.Slug)
            continue;
    }

    await platformDb.SaveChangesAsync();
}

static async Task EnsureUserAsync(
    PlatformDbContext platformDb,
    PasswordHasher<PlatformUser> passwordHasher,
    Guid? tenantId,
    string tenantSlug,
    string email,
    string fullName,
    string[] roles)
{
    if (await platformDb.PlatformUsers.AnyAsync(u => u.Email == email)) return;

    var user = PlatformUser.Create(tenantId, tenantSlug, email, fullName, roles);
    user.SetPasswordHash(passwordHasher.HashPassword(user, "Password123!"));
    await platformDb.PlatformUsers.AddAsync(user);
}
static async Task SeedPlatformTenantsAsync(PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory)
{
    if (await platformDb.Tenants.AnyAsync()) return;

    var tenants = new[]
    {
        Tenant.Create("Acme Bank", "acme-bank", "USD", "America/New_York"),
        Tenant.Create("Global Finance MFI", "global-finance", "BDT", "Asia/Dhaka"),
        Tenant.Create("Al-Baraka Islamic Finance", "al-baraka", "SAR", "Asia/Riyadh")
    };

    foreach (var tenant in tenants)
    {
        tenant.ConfigureDatabase(tenantDbFactory.BuildDatabaseName(tenant.Slug), tenantDbFactory.BuildConnectionString(tenant.Slug));
    }

    await platformDb.Tenants.AddRangeAsync(tenants);
    await platformDb.SaveChangesAsync();
}

static async Task SeedTenantDatabasesAsync(PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory)
{
    var tenants = await platformDb.Tenants.OrderBy(t => t.Name).ToListAsync();
    foreach (var tenant in tenants)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseConnectionString))
        {
            tenant.ConfigureDatabase(tenantDbFactory.BuildDatabaseName(tenant.Slug), tenantDbFactory.BuildConnectionString(tenant.Slug));
            await platformDb.SaveChangesAsync();
        }

        await using var tenantDb = tenantDbFactory.CreateDbContext(tenant);
        await tenantDb.Database.EnsureCreatedAsync();
        await SeedTenantConfigurationAsync(tenantDb, tenant);
        await EnsureTenantWorkflowAsync(tenantDb, tenant);
    }
}

static async Task SeedTenantConfigurationAsync(AppDbContext db, Tenant tenant)
{
    if (await db.TenantConfigurations.AnyAsync()
        || await db.CustomFields.AnyAsync()
        || await db.RuleDefinitions.AnyAsync()) return;

    if (tenant.Slug == "acme-bank")
    {
        await db.TenantConfigurations.AddRangeAsync(
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.FastTrackMaxAmount, "50000", ConfigValueType.Decimal, "seed"),
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.SlaFastTrackHours, "24", ConfigValueType.Integer, "seed"),
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.SlaStandardHours, "120", ConfigValueType.Integer, "seed"),
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.CreditScoreThreshold, "620", ConfigValueType.Integer, "seed"),
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.DtiRatioMax, "0.45", ConfigValueType.Decimal, "seed"),
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.RelationshipYearsForBypass, "3", ConfigValueType.Integer, "seed"));

        await db.CustomFields.AddRangeAsync(
            CustomField.Create(tenant.Id, "origination_channel", "Origination Channel", CustomFieldType.Select, isRequired: true, isSearchable: true),
            CustomField.Create(tenant.Id, "relationship_manager_id", "Relationship Manager", CustomFieldType.Text, isRequired: false));

        await db.RuleDefinitions.AddAsync(RuleDefinition.Create(
            tenant.Id,
            "Standard Eligibility Check",
            RuleCategory.Eligibility,
            """
            {
              "and": [
                {">=": [{"var": "applicant.creditScore"}, 620]},
                {"<=": [{"var": "applicant.dtiRatio"}, 0.45]}
              ]
            }
            """,
            RuleOutcome.FlagForReview,
            "seed"));
    }
    else if (tenant.Slug == "global-finance")
    {
        await db.CustomFields.AddRangeAsync(
            CustomField.Create(tenant.Id, "gst_registration_number", "GST Registration Number", CustomFieldType.Text, isRequired: false, isSearchable: true),
            CustomField.Create(tenant.Id, "gst_registered", "GST Registered?", CustomFieldType.Boolean, isRequired: true),
            CustomField.Create(tenant.Id, "number_of_dependents", "Number of Dependents", CustomFieldType.Number, isRequired: true));

        var gstAmountRule = RuleDefinition.Create(
            tenant.Id,
            "GST Registration Bonus - +20% Max Amount",
            RuleCategory.AmountAdjust,
            """{"==": [{"var": "custom.gst_registered"}, true]}""",
            RuleOutcome.AdjustMaxAmount,
            "seed");
        gstAmountRule.GetType().GetProperty("OutcomeData")!.SetValue(gstAmountRule, "1.20");
        await db.RuleDefinitions.AddAsync(gstAmountRule);
    }
    else if (tenant.Slug == "al-baraka")
    {
        await db.TenantConfigurations.AddRangeAsync(
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.BusinessCalendar, "[0,1,2,3,4]", ConfigValueType.Json, "seed", "Working days: Sunday to Thursday"),
            TenantConfiguration.Create(tenant.Id, TenantConfigKeys.DtiRatioMax, "0.55", ConfigValueType.Decimal, "seed", "Higher DTI allowed for government-backed products"));
    }

    await db.SaveChangesAsync();
}

static async Task EnsureTenantWorkflowAsync(AppDbContext db, Tenant tenant)
{
    if (tenant.Slug == "global-finance")
    {
        await EnsureGlobalFinanceWorkflowAsync(db, tenant);
        return;
    }

    await EnsureAcmeStyleWorkflowAsync(db, tenant);
}

static async Task EnsureAcmeStyleWorkflowAsync(AppDbContext db, Tenant tenant)
{
    var workflow = await db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.TenantId == tenant.Id && w.Name == "Acme Simple Approval");
    if (workflow is null)
    {
        workflow = WorkflowDefinition.Create(tenant.Id, "Acme Simple Approval", "seed", DateTime.UtcNow);
        workflow.SetSteps([
            new WorkflowStepDefinition { StepId = "data_entry", Name = "Data Entry", StepType = WorkflowStepType.DataEntry, AssigneeRoleCode = "LoanOfficer", NextStepIds = ["branch_review"] },
            new WorkflowStepDefinition { StepId = "branch_review", Name = "Branch Review", StepType = WorkflowStepType.Approval, AssigneeRoleCode = "BranchManager", SlaHours = 24, NextStepIds = ["approved", "rejected"] },
            new WorkflowStepDefinition { StepId = "approved", Name = "Approved", StepType = WorkflowStepType.Terminal, NextStepIds = [] },
            new WorkflowStepDefinition { StepId = "rejected", Name = "Rejected", StepType = WorkflowStepType.Terminal, NextStepIds = [] }
        ]);
        workflow.SetRoutingRules([
            new RoutingRule { FromStepId = "data_entry", ToStepId = "branch_review", Priority = 1, Description = "Completed proposals go to branch review." },
            new RoutingRule { FromStepId = "branch_review", ToStepId = "approved", Priority = 1, Description = "Simple approval path." }
        ]);
        workflow.Activate(DateTime.UtcNow);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
    }

    if (!await db.LoanProducts.AnyAsync(p => p.TenantId == tenant.Id && p.Code == "ACME-SIMPLE"))
    {
        db.LoanProducts.Add(LoanProduct.Create(tenant.Id, "Acme Simple Loan", "ACME-SIMPLE", LoanProductType.Personal, 1000, 100000, workflow.Id));
        await db.SaveChangesAsync();
    }
}

static async Task EnsureGlobalFinanceWorkflowAsync(AppDbContext db, Tenant tenant)
{
    var workflow = await db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.TenantId == tenant.Id && w.Name == "Global Finance Microfinance Approval");
    if (workflow is null)
    {
        workflow = WorkflowDefinition.Create(tenant.Id, "Global Finance Microfinance Approval", "seed", DateTime.UtcNow);
        workflow.SetSteps([
            new WorkflowStepDefinition { StepId = "field_officer_intake", Name = "Field Officer Intake", StepType = WorkflowStepType.DataEntry, AssigneeRoleCode = "FieldOfficer", NextStepIds = ["gst_screening"] },
            new WorkflowStepDefinition { StepId = "gst_screening", Name = "GST Screening", StepType = WorkflowStepType.AutomatedCheck, SlaHours = 4, NextStepIds = ["branch_credit_review", "group_lead_review"] },
            new WorkflowStepDefinition { StepId = "branch_credit_review", Name = "Branch Credit Review", StepType = WorkflowStepType.Approval, AssigneeRoleCode = "BranchCreditOfficer", SlaHours = 24, NextStepIds = ["approved", "rejected"] },
            new WorkflowStepDefinition { StepId = "group_lead_review", Name = "Group Lead Review", StepType = WorkflowStepType.Approval, AssigneeRoleCode = "GroupLead", SlaHours = 12, NextStepIds = ["approved", "rejected"] },
            new WorkflowStepDefinition { StepId = "approved", Name = "Approved", StepType = WorkflowStepType.Terminal, NextStepIds = [] },
            new WorkflowStepDefinition { StepId = "rejected", Name = "Rejected", StepType = WorkflowStepType.Terminal, NextStepIds = [] }
        ]);
        workflow.SetRoutingRules([
            new RoutingRule { FromStepId = "field_officer_intake", ToStepId = "gst_screening", Priority = 1, Description = "All microfinance proposals receive automated GST screening." },
            new RoutingRule { FromStepId = "gst_screening", ToStepId = "branch_credit_review", ConditionExpression = "CustomField['gst_registered'] == true", Priority = 1, Description = "GST-registered borrowers move to branch credit review." },
            new RoutingRule { FromStepId = "gst_screening", ToStepId = "group_lead_review", Priority = 2, Description = "Non-GST borrowers receive group lead review." },
            new RoutingRule { FromStepId = "branch_credit_review", ToStepId = "approved", Priority = 1, Description = "Simple approval path after branch credit review." },
            new RoutingRule { FromStepId = "group_lead_review", ToStepId = "approved", Priority = 1, Description = "Simple approval path after group lead review." }
        ]);
        workflow.Activate(DateTime.UtcNow);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
    }

    if (!await db.LoanProducts.AnyAsync(p => p.TenantId == tenant.Id && p.Code == "GLOBAL-MICRO"))
    {
        db.LoanProducts.Add(LoanProduct.Create(tenant.Id, "Global Micro Enterprise Loan", "GLOBAL-MICRO", LoanProductType.Microfinance, 5000, 250000, workflow.Id));
        await db.SaveChangesAsync();
    }
}
