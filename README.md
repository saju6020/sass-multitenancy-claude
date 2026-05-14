# LoanProposal SaaS — C# Multitenant Blueprint

A production-pattern blueprint for a configuration-driven, multitenant loan proposal platform built on ASP.NET Core 8, Entity Framework Core, and PostgreSQL.

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                    HTTP Request                           │
│          acme-bank.loanplatform.io/api/applications       │
└─────────────────────┬────────────────────────────────────┘
                      │
              TenantResolutionMiddleware
              (resolves TenantId from JWT / subdomain / header)
                      │
              ┌───────▼────────┐
              │  ITenantContext │  ← Scoped, per-request
              │  TenantId = X   │
              └───────┬────────┘
                      │ injected into
        ┌─────────────┼─────────────────────┐
        │             │                     │
   AppDbContext   Repositories         Domain Services
   (Global Query  (auto-scoped to      (WorkflowEngine,
    Filters apply  current tenant)      RuleEngine, SLA)
    TenantId to
    every query)
```

## Project Structure

```
LoanProposal/
├── src/
│   ├── LoanProposal.Core/              # Domain model — no dependencies
│   │   ├── Entities/
│   │   │   ├── Tenant.cs               # Tenant aggregate
│   │   │   ├── TenantConfiguration.cs  # Key-value config per tenant
│   │   │   ├── CustomField.cs          # Unified field registry
│   │   │   ├── LoanProduct.cs          # Tenant-configured loan products
│   │   │   ├── WorkflowDefinition.cs   # Versioned state machine config
│   │   │   ├── RuleDefinition.cs       # JSON Logic eligibility/pricing rules
│   │   │   ├── LoanApplication.cs      # Core aggregate
│   │   │   └── SupportingEntities.cs   # Applicant, StateTransition, Document
│   │   ├── Interfaces/
│   │   │   └── IRepositories.cs        # Repository + domain service contracts
│   │   └── Enums/
│   │       └── LoanApplicationStatus.cs
│   │
│   ├── LoanProposal.Infrastructure/    # EF Core, rule engine, schedulers
│   │   ├── Data/
│   │   │   └── AppDbContext.cs         # EF context with global query filters
│   │   ├── Repositories/
│   │   │   ├── TenantScopedRepository.cs  # Base class — auto-scoped
│   │   │   └── Repositories.cs            # All repository implementations
│   │   └── Services/
│   │       ├── WorkflowEngine.cs       # Config-driven state machine executor
│   │       ├── JsonLogicRuleEngine.cs  # Sandboxed expression evaluator
│   │       ├── TenantContext.cs        # HTTP/System/Platform contexts
│   │       └── SlaTimerService.cs      # Business-calendar-aware SLA timers
│   │
│   ├── LoanProposal.Application/       # CQRS with MediatR
│   │   └── Commands/
│   │       └── LoanApplicationCommands.cs
│   │
│   └── LoanProposal.API/               # ASP.NET Core Web API
│       ├── Controllers/
│       │   └── Controllers.cs          # Application, Configuration, Platform
│       ├── Middleware/
│       │   └── TenantResolutionMiddleware.cs
│       └── Program.cs                  # DI + middleware pipeline
└── README.md
```

---

## Key Architectural Decisions

### 1. Tenant Isolation via EF Core Global Query Filters

Every tenant-scoped entity has a `TenantId` column. `AppDbContext` applies a global query filter to every entity:

```csharp
// In AppDbContext.OnModelCreating:
e.HasQueryFilter(a => a.TenantId == _tenantContext.TenantId);
```

This means **no repository method can accidentally leak cross-tenant data** — EF Core enforces it at the SQL level. The `ITenantContext` is injected into `AppDbContext` as a scoped dependency, resolved from the current HTTP request.

### 2. Unified Field Registry (Custom Fields)

The `CustomField` entity is the single source of truth for all tenant-defined fields. Every subsystem references fields by `FieldKey`:

- **Rule Engine**: `{"var": "custom.gst_registered"}` 
- **Workflow routing**: `CustomField['gst_registered'] == true`
- **Document templates**: `{{custom.gst_registration_number}}`
- **Report queries**: `SearchByCustomFieldAsync("gst_registered", "true")`
- **Storage**: JSONB `custom_data` column on `LoanApplication`

This prevents the "four different ways to reference the same field" problem described in the architecture document.

### 3. Workflow Versioning

`WorkflowDefinition` is versioned. When a tenant changes their workflow:
1. A new version is created (old version untouched)
2. New version starts **inactive** — configurable `EffectiveFrom` date
3. `LoanApplication` stores the `WorkflowDefinitionId` at submission time
4. In-flight applications are always evaluated against their submission-time version

```csharp
// Get the workflow version that was active when this application was submitted:
var workflow = await _workflowRepo.GetVersionActiveAtAsync(
    workflowDefinitionId, application.SubmittedAt);
```

### 4. Sandboxed Rule Engine (JSON Logic)

Rules are stored as JSON Logic expressions — a safe, serializable, conflict-checkable format that cannot cause infinite loops or access external systems:

```json
{
  "and": [
    {">=": [{"var": "applicant.creditScore"}, 620]},
    {"<=": [{"var": "applicant.dtiRatio"}, 0.45]},
    {"==": [{"var": "product.type"}, "SME"]}
  ]
}
```

### 5. Tenant Resolution Strategies

```
JWT claim tenant_id  →  acme-bank.loanplatform.io (subdomain)  →  X-Tenant-Id header
```

The middleware registers `ITenantContext` into `HttpContext.Items` before any service accesses it.

### 6. Business-Calendar-Aware SLA Timers

SLA deadlines respect:
- Tenant-configured working days (e.g. Sun–Thu for Middle East tenants)
- Tenant-uploaded public holiday lists
- Tenant timezone

Timers are persisted via Hangfire (durable — survive restarts), keyed as `sla:{tenantId}:{applicationId}:{stepId}` for selective cancellation when applications advance.

---

## Running Locally

### Prerequisites
- .NET 8 SDK
- PostgreSQL 15+ (for JSONB support)
- (Optional) Redis for distributed caching

### Setup

```bash
# Clone and restore
cd LoanProposal
dotnet restore

# Set your connection string
cd src/LoanProposal.API
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Database=loanproposal;Username=postgres;Password=your_password"

# Run (auto-migrates and seeds demo data in Development)
dotnet run

# Swagger UI available at:
# https://localhost:5001/swagger
```

### Demo Tenants Seeded

| Tenant | Slug | Currency | Notes |
|--------|------|----------|-------|
| Acme Bank | `acme-bank` | USD | Standard approval workflow, 45% DTI max |
| Global Finance MFI | `global-finance` | BDT | GST custom field, GST-based amount boost |
| Al-Baraka Islamic Finance | `al-baraka` | SAR | Sun–Thu working days, 55% DTI for govt-backed |

### Sample API Calls

```bash
# Submit a loan application (with tenant identified via header for dev)
curl -X POST https://localhost:5001/api/applications \
  -H "Authorization: Bearer {jwt}" \
  -H "X-Tenant-Id: {acme-bank-tenant-id}" \
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

# Search by custom field
curl "https://localhost:5001/api/applications/search?fieldKey=origination_channel&value=Branch" \
  -H "Authorization: Bearer {jwt}"

# Configure a workflow
curl -X POST https://localhost:5001/api/configuration/workflows \
  -H "Authorization: Bearer {admin-jwt}" \
  -d '{
    "workflowName": "SME Fast Track v2",
    "steps": [...],
    "routingRules": [
      {
        "fromStepId": "data_entry",
        "toStepId": "branch_manager_approval",
        "conditionExpression": "LoanAmount <= 50000",
        "priority": 1
      },
      {
        "fromStepId": "data_entry",
        "toStepId": "credit_committee",
        "conditionExpression": "LoanAmount > 50000",
        "priority": 2
      }
    ],
    "effectiveFrom": "2025-01-01T00:00:00Z"
  }'
```

---

## Extending the Blueprint

### Add a new integration step (e.g. CBS disbursement)

1. Add a `WorkflowStepType.Integration` step to a workflow definition
2. Store endpoint/credentials in `TenantConfiguration` (encrypted)
3. `WorkflowEngine.AdvanceAsync` detects `Integration` type → calls `IIntegrationService`
4. On failure → set application to `PendingDisbursement`, trigger ops alert

### Add a new custom field type

1. Add value to `CustomFieldType` enum
2. Add validation logic in `CustomField.Create`
3. Update `LoanApplicationContext` builder in `JsonLogicRuleEngine`
4. Add UI component mapping in the frontend

### Add a new tenant

```csharp
// Via platform admin API — no code changes needed
POST /platform/tenants
{
  "name": "Pacific Lending Co",
  "slug": "pacific-lending",
  "currency": "AUD",
  "timezone": "Australia/Sydney"
}
```

---

## Production Checklist

- [ ] Replace `JsonLogicRuleEngine` stub with a real JSON Logic library (`JsonLogic.Net`)
- [ ] Encrypt tenant API credentials in `TenantConfiguration` (Azure Key Vault / AWS KMS)
- [ ] Add GIN index on `custom_data` JSONB column for search performance
- [ ] Implement `IDocumentGenerator` with a real template engine (Scriban / Handlebars.Net)
- [ ] Replace Hangfire stub in `SlaTimerService` with real job scheduling
- [ ] Add row-level security policy at the PostgreSQL level as a defense-in-depth measure
- [ ] Implement configuration diff/audit log for compliance officer review
- [ ] Add configuration RBAC (tenant admins vs compliance vs IT admins)
- [ ] Add conflict detection in `ValidateRule` endpoint (Z3 SMT solver or overlap heuristics)
- [ ] Set up per-tenant staging environment for workflow testing before activation
