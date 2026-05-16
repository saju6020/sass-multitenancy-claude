using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using LoanProposal.Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
using TenantRegistration.API.Controllers;
using TenantRegistration.API.Data;
using TenantRegistration.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("TenantRegistration.API")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<PasswordHasher<PlatformUser>>();
builder.Services.AddSingleton<TenantDatabaseNameBuilder>();

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthController.GetSigningKey(builder.Configuration))),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(RoleNames.PlatformAdmin, policy => policy.RequireRole(RoleNames.PlatformAdmin));
});

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/platform/tenants"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TenantRegistration" })).AllowAnonymous();

app.MapPost("/api/platform/tenants", async (
    CreateTenantDto request,
    PlatformDbContext db,
    TenantDatabaseNameBuilder tenantDbNameBuilder,
    CancellationToken ct) =>
{
    var slug = NormalizeSlug(request.Slug);
    if (string.IsNullOrWhiteSpace(slug))
        return Results.BadRequest(new { error = "Slug must contain at least one letter or number." });
    if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        return Results.Conflict(new { error = $"Slug '{slug}' is already taken." });

    var tenant = Tenant.Create(request.Name, slug, request.Currency, request.Timezone);
    tenant.ConfigureDatabase(tenantDbNameBuilder.BuildDatabaseName(slug), tenantDbNameBuilder.BuildConnectionString(slug));
    db.Tenants.Add(tenant);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToDescriptor(tenant, tenantDbNameBuilder));
}).RequireAuthorization(RoleNames.PlatformAdmin);

app.MapGet("/internal/tenants", async (HttpContext context, PlatformDbContext db, TenantDatabaseNameBuilder builder, CancellationToken ct) =>
{
    if (!HasInternalAccess(context)) return Results.Unauthorized();
    var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    return Results.Ok(tenants.Select(t => ToOption(t)));
});

app.MapGet("/internal/tenants/{tenantId:guid}/services/loan-proposal", async (Guid tenantId, HttpContext context, PlatformDbContext db, TenantDatabaseNameBuilder builder, CancellationToken ct) =>
{
    if (!HasInternalAccess(context)) return Results.Unauthorized();
    var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
    return tenant is null ? Results.NotFound() : Results.Ok(ToDescriptor(tenant, builder));
});

app.MapGet("/internal/tenants/slug/{slug}/services/loan-proposal", async (string slug, HttpContext context, PlatformDbContext db, TenantDatabaseNameBuilder builder, CancellationToken ct) =>
{
    if (!HasInternalAccess(context)) return Results.Unauthorized();
    var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, ct);
    return tenant is null ? Results.NotFound() : Results.Ok(ToDescriptor(tenant, builder));
});

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsurePlatformSchemaAsync(db);
    await SeedTenantsAndUsersAsync(
        db,
        scope.ServiceProvider.GetRequiredService<TenantDatabaseNameBuilder>(),
        scope.ServiceProvider.GetRequiredService<PasswordHasher<PlatformUser>>());
}

app.Run();

bool HasInternalAccess(HttpContext context)
{
    var expected = builder.Configuration["InternalApiKey"];
    return !string.IsNullOrWhiteSpace(expected)
        && context.Request.Headers.TryGetValue("X-Internal-Api-Key", out var actual)
        && actual == expected;
}

static TenantOptionDto ToOption(Tenant tenant) =>
    new(tenant.Id, tenant.Name, tenant.Slug, tenant.DefaultCurrency);

static TenantServiceDescriptor ToDescriptor(Tenant tenant, TenantDatabaseNameBuilder builder) =>
    new(
        tenant.Id,
        tenant.Name,
        tenant.Slug,
        tenant.DefaultCurrency,
        tenant.DefaultTimezone,
        "LoanProposal",
        string.IsNullOrWhiteSpace(tenant.DatabaseName) ? builder.BuildDatabaseName(tenant.Slug) : tenant.DatabaseName,
        builder.ResolveConnectionString(tenant),
        tenant.IsActive);

static async Task EnsurePlatformSchemaAsync(PlatformDbContext db)
{
    await db.Database.ExecuteSqlRawAsync("""
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

static async Task SeedTenantsAndUsersAsync(PlatformDbContext db, TenantDatabaseNameBuilder builder, PasswordHasher<PlatformUser> passwordHasher)
{
    if (!await db.Tenants.AnyAsync())
    {
        var tenants = new[]
        {
            Tenant.Create("Acme Bank", "acme-bank", "USD", "America/New_York"),
            Tenant.Create("Global Finance MFI", "global-finance", "BDT", "Asia/Dhaka"),
            Tenant.Create("Al-Baraka Islamic Finance", "al-baraka", "SAR", "Asia/Riyadh")
        };

        foreach (var tenant in tenants)
            tenant.ConfigureDatabase(builder.BuildDatabaseName(tenant.Slug), builder.BuildConnectionString(tenant.Slug));

        db.Tenants.AddRange(tenants);
        await db.SaveChangesAsync();
    }

    var allTenants = await db.Tenants.OrderBy(t => t.Name).ToListAsync();
    await EnsureTenantDefaultsAsync(db, builder, "Acme Bank", "acme-bank", "USD", "America/New_York");
    await EnsureTenantDefaultsAsync(db, builder, "Global Finance MFI", "global-finance", "BDT", "Asia/Dhaka");
    await EnsureTenantDefaultsAsync(db, builder, "Al-Baraka Islamic Finance", "al-baraka", "SAR", "Asia/Riyadh");
    allTenants = await db.Tenants.OrderBy(t => t.Name).ToListAsync();

    await EnsureUserAsync(db, passwordHasher, null, "__platform__", "platform@loanproposal.local", "Platform Administrator", [RoleNames.PlatformAdmin]);

    foreach (var tenant in allTenants)
    {
        await EnsureUserAsync(db, passwordHasher, tenant.Id, tenant.Slug, $"admin@{tenant.Slug}.local", $"{tenant.Name} Admin", [RoleNames.TenantAdmin]);
        await EnsureUserAsync(db, passwordHasher, tenant.Id, tenant.Slug, $"officer@{tenant.Slug}.local", $"{tenant.Name} Loan Officer", [RoleNames.LoanOfficer]);
        await EnsureUserAsync(db, passwordHasher, tenant.Id, tenant.Slug, $"reviewer@{tenant.Slug}.local", $"{tenant.Name} Reviewer", [RoleNames.Reviewer]);
        await EnsureUserAsync(db, passwordHasher, tenant.Id, tenant.Slug, $"approver@{tenant.Slug}.local", $"{tenant.Name} Approver", [RoleNames.Approver]);
    }

    await db.SaveChangesAsync();
}

static async Task EnsureTenantDefaultsAsync(PlatformDbContext db, TenantDatabaseNameBuilder builder, string name, string slug, string currency, string timezone)
{
    var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
    if (tenant is null)
    {
        tenant = Tenant.Create(name, slug, currency, timezone);
        db.Tenants.Add(tenant);
    }

    Set(tenant, nameof(Tenant.Name), name);
    Set(tenant, nameof(Tenant.DefaultCurrency), currency);
    Set(tenant, nameof(Tenant.DefaultTimezone), timezone);
    Set(tenant, nameof(Tenant.IsActive), true);
    tenant.ConfigureDatabase(builder.BuildDatabaseName(slug), builder.BuildConnectionString(slug));
}

static string NormalizeSlug(string slug)
{
    var normalized = Regex.Replace(slug.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    return normalized;
}

static async Task EnsureUserAsync(PlatformDbContext db, PasswordHasher<PlatformUser> passwordHasher, Guid? tenantId, string tenantSlug, string email, string fullName, string[] roles)
{
    if (await db.PlatformUsers.AnyAsync(u => u.Email == email)) return;

    var user = PlatformUser.Create(tenantId, tenantSlug, email, fullName, roles);
    user.SetPasswordHash(passwordHasher.HashPassword(user, "Password123!"));
    await db.PlatformUsers.AddAsync(user);
}

static void Set<T>(object target, string propertyName, T value)
{
    var property = target.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().Name}.");
    property.SetValue(target, value);
}
