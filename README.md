# LoanProposal SaaS - C# Multitenant Blueprint

A production-pattern blueprint for a configuration-driven, multitenant loan proposal platform built on ASP.NET Core 8, Entity Framework Core, PostgreSQL, MediatR, and Elsa workflow components.

The current implementation uses a **platform database plus one physical database per tenant**. The platform database is the control plane for tenant registration. Each tenant database stores that tenant's loan products, custom fields, rules, workflows, applicants, and loan applications.

---

## Architecture Overview

```text
HTTP request
  |
  |-- /platform/* endpoints
  |     PlatformDbContext
  |     Tenant registry, database name, connection string
  |
  |-- /api/* endpoints
        TenantResolutionMiddleware
          1. JWT claim: tenant_id
          2. Subdomain: acme-bank.loanplatform.io
          3. Header: X-Tenant-Id
        |
        ITenantContext
          TenantId
          TenantSlug
          ConnectionString
        |
        AppDbContext
          Uses the resolved tenant database connection string
        |
        Tenant-scoped repositories, rule engine, workflow engine, SLA services
```

### Database Model

```text
Platform database
  Tenants
    Id
    Name
    Slug
    DefaultCurrency
    DefaultTimezone
    DatabaseName
    DatabaseConnectionString
    IsActive

Tenant database: loanproposal_acme_bank
  TenantConfigurations
  CustomFields
  RuleDefinitions
  WorkflowDefinitions
  LoanProducts
  Applicants
  LoanApplications
  ApplicationStateTransitions
  ApplicationDocuments

Tenant database: loanproposal_global_finance
  Same schema, isolated physical database
```

Tenant-scoped entities still carry `TenantId` as metadata and for indexes, but the primary isolation boundary is the tenant database itself.

---

## Project Structure

```text
LoanProposal/
  src/
    LoanProposal.Core/
      Entities/
        Tenant.cs                  # Platform tenant registry entity
        TenantConfiguration.cs     # Tenant database key-value settings
        CustomField.cs             # Tenant-defined field registry
        LoanProduct.cs             # Tenant-configured loan products
        WorkflowDefinition.cs      # Versioned workflow configuration
        RuleDefinition.cs          # JSON Logic rules
        LoanApplication.cs         # Loan proposal aggregate
        SupportingEntities.cs      # Applicant, transitions, documents
      Interfaces/
        IRepositories.cs           # Repository and service contracts
      Enums/
        LoanApplicationStatus.cs

    LoanProposal.Infrastructure/
      Data/
        PlatformDbContext.cs       # Platform DB, tenant registry only
        AppDbContext.cs            # Tenant DB operational data
        TenantDbContextFactory.cs  # Builds tenant DB names/connections
      Repositories/
        TenantScopedRepository.cs
        Repositories.cs
      Services/
        TenantContext.cs           # HTTP, system, and platform contexts
        JsonLogicRuleEngine.cs
        ElsaWorkflowEngine.cs
        SlaTimerService.cs
        LoggingNotificationService.cs

    LoanProposal.Application/
      Commands/
        LoanApplicationCommands.cs # MediatR command handlers

    LoanProposal.API/
      Controllers/
        Controllers.cs             # API endpoints
        MvcControllers.cs          # MVC admin/demo screens
      Middleware/
        TenantResolutionMiddleware.cs
      Models/
        MvcViewModels.cs
      Views/
        Dashboard/
        Tenants/
        Configuration/
        Applications/
      Program.cs
```

---

## Key Architectural Decisions

### 1. Platform DB + Tenant DB Segregation

`PlatformDbContext` stores only platform-owned tenant registry data. `AppDbContext` stores operational business data and is created with the current tenant's database connection string.

In `Program.cs`, `AppDbContext` is configured per request:

```csharp
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var tenantContext = serviceProvider.GetRequiredService<ITenantContext>();
    options.UseNpgsql(tenantContext.ConnectionString);
});
```

This means tenant data is isolated at the physical database level instead of relying only on EF Core query filters.

### 2. Tenant Registration and Provisioning

Tenants can be created through:

- MVC UI: `/platform-tenants`
- API: `POST /platform/tenants`

When a tenant is created:

1. The slug is normalized.
2. A database name is generated, such as `loanproposal_acme_bank`.
3. A tenant connection string is built from `ConnectionStrings:DefaultConnection`.
4. The tenant is saved in the platform database.
5. The tenant database is created with `EnsureCreatedAsync()`.

The tenant database is intentionally left intact when a tenant is removed from the MVC registry screen.

### 3. Tenant Resolution

API requests are resolved by `TenantResolutionMiddleware` using this order:

```text
JWT claim tenant_id -> subdomain -> X-Tenant-Id header
```

After resolution, the middleware stores an `HttpTenantContext` in `HttpContext.Items`. That context carries:

- `TenantId`
- `TenantSlug`
- `ConnectionString`

Platform endpoints can run without a tenant database by using `PlatformTenantContext`.

### 4. Tenant Configuration

The MVC configuration screen at `/tenant-configuration` allows tenant-specific setup for:

- Settings in `TenantConfiguration`
- Custom fields
- JSON Logic rules
- Workflow definitions

The selected tenant is opened through `TenantDbContextFactory`, so configuration changes are written into that tenant's own database.

### 5. Unified Field Registry

`CustomField` is the registry for tenant-defined fields. Every subsystem uses the same `FieldKey`:

- Rule engine: `{"var": "custom.gst_registered"}`
- Workflow routing: `CustomField['gst_registered'] == true`
- Reporting/search: `SearchByCustomFieldAsync("gst_registered", "true")`
- Storage: JSONB `custom_data` on `LoanApplication`

This keeps tenant-specific fields consistent across rules, workflows, forms, and reporting.

### 6. Workflow and Rules

`WorkflowDefinition` stores versioned workflow steps and routing rules as JSON. New loan applications store the workflow definition selected at submission time.

Rules are stored as JSON Logic expressions:

```json
{
  "and": [
    { ">=": [{ "var": "applicant.creditScore" }, 620] },
    { "<=": [{ "var": "applicant.dtiRatio" }, 0.45] }
  ]
}
```

The app currently contains `JsonLogicRuleEngine` and `ElsaWorkflowEngine` service implementations for evaluating rules and advancing workflows.

### 7. MVC Admin and Demo UI

The project includes server-rendered MVC screens for operating the blueprint without a separate frontend:

- `/dashboard` - tenant summaries and counts from each tenant database
- `/platform-tenants` - register, edit, and remove tenants from the platform registry
- `/tenant-configuration` - manage tenant settings, fields, rules, and workflows
- `/loan-applications` - create, view, advance, approve, reject, and delete proposals per tenant

---

## Running Locally

### Prerequisites

- .NET 8 SDK
- PostgreSQL 15+ for JSONB support

### Setup

```bash
cd LoanProposal
dotnet restore

cd src/LoanProposal.API
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Database=loanproposal_platform;Username=postgres;Password=your_password"

dotnet run
```

In Development, the app attempts to:

1. Create the platform database schema.
2. Seed demo tenants into the platform database.
3. Create each demo tenant database.
4. Seed each tenant database with settings, fields, rules, workflows, and products.

Swagger is available at:

```text
https://localhost:5001/swagger
```

MVC screens are available at:

```text
https://localhost:5001/dashboard
https://localhost:5001/platform-tenants
https://localhost:5001/tenant-configuration
https://localhost:5001/loan-applications
```

### Seeded Login Users

Development seeding creates these users with password `Password123!`:

| Email | Role | Tenant |
|-------|------|--------|
| `platform@loanproposal.local` | `PlatformAdmin` | Platform |
| `admin@acme-bank.local` | `TenantAdmin` | Acme Bank |
| `officer@acme-bank.local` | `LoanOfficer` | Acme Bank |
| `reviewer@acme-bank.local` | `Reviewer` | Acme Bank |
| `approver@acme-bank.local` | `Approver` | Acme Bank |
| `admin@global-finance.local` | `TenantAdmin` | Global Finance MFI |
| `officer@global-finance.local` | `LoanOfficer` | Global Finance MFI |
| `reviewer@global-finance.local` | `Reviewer` | Global Finance MFI |
| `approver@global-finance.local` | `Approver` | Global Finance MFI |
| `admin@al-baraka.local` | `TenantAdmin` | Al-Baraka Islamic Finance |
| `officer@al-baraka.local` | `LoanOfficer` | Al-Baraka Islamic Finance |
| `reviewer@al-baraka.local` | `Reviewer` | Al-Baraka Islamic Finance |
| `approver@al-baraka.local` | `Approver` | Al-Baraka Islamic Finance |

MVC login is available at:

```text
https://localhost:5001/account/login
```

API token login is available at:

```text
POST https://localhost:5001/auth/token
```

An HTTP test collection is included at `tests/LoanProposal.Api.http`.

---

## Demo Tenants

| Tenant | Slug | Currency | Tenant Database | Notes |
|--------|------|----------|-----------------|-------|
| Acme Bank | `acme-bank` | USD | `loanproposal_acme_bank` | Simple approval workflow, standard eligibility rule |
| Global Finance MFI | `global-finance` | BDT | `loanproposal_global_finance` | GST fields, GST amount adjustment rule, microfinance workflow |
| Al-Baraka Islamic Finance | `al-baraka` | SAR | `loanproposal_al_baraka` | Sunday-Thursday business calendar, higher DTI setting |

---

## Sample API Calls

### Create a Tenant

```bash
curl -X POST https://localhost:5001/platform/tenants \
  -H "Authorization: Bearer {platform-admin-jwt}" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Pacific Lending Co",
    "slug": "pacific-lending",
    "currency": "AUD",
    "timezone": "Australia/Sydney"
  }'
```

### Submit a Loan Application

```bash
curl -X POST https://localhost:5001/api/applications \
  -H "Authorization: Bearer {jwt}" \
  -H "X-Tenant-Id: {tenant-id}" \
  -H "Content-Type: application/json" \
  -d '{
    "loanProductId": "...",
    "applicantId": "...",
    "requestedAmount": 45000,
    "requestedTenureMonths": 36,
    "customFields": {
      "origination_channel": "Branch",
      "relationship_manager_id": "RM-042"
    }
  }'
```

### Search by Custom Field

```bash
curl "https://localhost:5001/api/applications/search?fieldKey=origination_channel&value=Branch" \
  -H "Authorization: Bearer {jwt}" \
  -H "X-Tenant-Id: {tenant-id}"
```

### Configure a Workflow

```bash
curl -X POST https://localhost:5001/api/configuration/workflows \
  -H "Authorization: Bearer {tenant-admin-jwt}" \
  -H "X-Tenant-Id: {tenant-id}" \
  -H "Content-Type: application/json" \
  -d '{
    "workflowName": "SME Fast Track v2",
    "steps": [],
    "routingRules": [],
    "effectiveFrom": "2026-01-01T00:00:00Z"
  }'
```

---

## Extending the Blueprint

### Add a New Tenant

No code change is required. Create the tenant through `/platform-tenants` or `POST /platform/tenants`. The platform stores the tenant registry row and provisions the tenant database.

### Add a New Tenant Setting

Add a row in `/tenant-configuration` for the selected tenant. Settings are stored in the selected tenant database in `TenantConfigurations`.

### Add a New Custom Field Type

1. Add the value to `CustomFieldType`.
2. Add validation/default behavior in `CustomField`.
3. Update MVC form rendering or frontend component mapping.
4. Include the value in `LoanApplicationContext` if rules or templates need it.

### Add a New Workflow Step Type

1. Add a value to `WorkflowStepType`.
2. Update `ElsaWorkflowEngine` or workflow execution logic to handle it.
3. Store tenant-specific step configuration inside `WorkflowDefinition.StepsJson`.

---

## Production Checklist

- [ ] Replace `EnsureCreatedAsync()` provisioning with migrations per platform and tenant database.
- [ ] Encrypt tenant database connection strings and tenant API credentials.
- [ ] Add tenant database lifecycle operations for backup, restore, archival, and deletion.
- [ ] Add RBAC around MVC admin screens and tenant configuration screens.
- [ ] Add audit logging for tenant registration, configuration changes, workflow changes, and rule changes.
- [ ] Add GIN indexes on JSONB fields such as `custom_data`.
- [ ] Complete conflict detection in `ValidateRule` using overlap heuristics or an SMT solver.
- [ ] Implement a real document generation service.
- [ ] Replace any remaining SLA scheduling stubs with durable Hangfire jobs.
- [ ] Add integration tests proving cross-tenant database isolation.
- [ ] Add per-tenant staging or preview mode before activating workflows.
