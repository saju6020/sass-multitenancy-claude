# LoanProposal SaaS - Split-Service Multitenant Blueprint

This repository now models the real deployment shape as two services:

- **TenantRegistration Service**: owns tenants, users, roles, login, JWT issuance, and service/database registration metadata.
- **LoanProposal Service**: owns tenant-wise workspace configuration and the loan proposal lifecycle.

TenantRegistration is the platform/control plane. LoanProposal is a tenant-aware business service that asks TenantRegistration where a tenant's LoanProposal database lives.

---

## Current Architecture

```text
User / API client
  |
  |-- login/token
  v
TenantRegistration.API
  Platform database: tenantregistration_platform
    Tenants
    PlatformUsers
    Role claims
    LoanProposal database metadata
  |
  |-- JWT: tenant_id, tenant_slug, roles
  v
LoanProposal.API
  TenantResolutionMiddleware
    Reads tenant_id / tenant_slug / X-Tenant-Id
    Calls TenantRegistration internal registry endpoint
    Receives LoanProposal DB connection string
  |
  v
Tenant-specific LoanProposal database
  loanproposal_acme_bank
  loanproposal_global_finance
  loanproposal_al_baraka
```

The services communicate through `Shared.Contracts` DTOs, not through a shared platform DbContext.

Draw.io diagram:

```text
docs/split-service-architecture.drawio
docs/saas-multitenancy-architecture.drawio
```

Mermaid architecture diagrams:

```text
docs/saas-multitenancy-architecture.md
```

---

## Projects

```text
src/
  TenantRegistration.API/
    Owns tenant registration, users, roles, login, JWT token issuance,
    and internal tenant service metadata endpoints.

  LoanProposal.API/
    Owns MVC/API screens for workspace configuration and loan proposals.
    Delegates login/token validation to TenantRegistration.

  LoanProposal.Core/
    Loan proposal domain entities and contracts.

  LoanProposal.Application/
    MediatR commands and application handlers.

  LoanProposal.Infrastructure/
    LoanProposal tenant database DbContext, repositories, rule/workflow services.

  Shared.Contracts/
    Cross-service DTOs, role constants, and claim constants.
```

---

## Service Responsibilities

### TenantRegistration.API

Owns:

- tenant registration
- tenant lifecycle metadata
- platform users
- user roles
- password hashing
- `/auth/token`
- `/account/login`
- `/platform/tenants`
- internal tenant registry lookup endpoints

Development seed users use password `Password123!`.

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

### LoanProposal.API

Owns:

- workspace configuration per tenant
- tenant settings
- custom fields
- JSON Logic rules
- workflow definitions
- loan applications
- proposal review/approve/reject flow

LoanProposal no longer owns tenant/user registration. It consumes TenantRegistration via `ITenantRegistryClient`.

---

## Running Locally

Start TenantRegistration first:

```bash
dotnet run --project src/TenantRegistration.API --urls http://localhost:5101
```

Then start LoanProposal:

```bash
dotnet run --project src/LoanProposal.API --urls http://localhost:5131
```

TenantRegistration endpoints:

```text
http://localhost:5101/account/login
http://localhost:5101/platform/tenants
http://localhost:5101/swagger
```

LoanProposal endpoints:

```text
http://localhost:5131/account/login
http://localhost:5131/dashboard
http://localhost:5131/tenant-configuration
http://localhost:5131/loan-applications
http://localhost:5131/swagger
```

The LoanProposal login page is now a local MVC sign-in bridge: it sends credentials to TenantRegistration `/auth/token`, then stores the returned claims in a local LoanProposal cookie.

---

## Internal Registry Contract

LoanProposal resolves tenant service metadata through TenantRegistration:

```text
GET /internal/tenants/{tenantId}/services/loan-proposal
GET /internal/tenants/slug/{slug}/services/loan-proposal
GET /internal/tenants
```

The internal calls use:

```text
X-Internal-Api-Key: dev-internal-registry-key
```

Example response:

```json
{
  "tenantId": "00000000-0000-0000-0000-000000000000",
  "tenantName": "Acme Bank",
  "tenantSlug": "acme-bank",
  "currency": "USD",
  "timezone": "America/New_York",
  "serviceName": "LoanProposal",
  "databaseName": "loanproposal_acme_bank",
  "connectionString": "...",
  "isActive": true
}
```

---

## Roles

Roles are issued by TenantRegistration and enforced by LoanProposal:

- `PlatformAdmin`: platform tenant registration
- `TenantAdmin`: workspace configuration
- `LoanOfficer`: create loan proposals
- `Reviewer`: review proposals
- `Approver`: approve/reject proposals
- `Auditor`: reserved for read-only audit access

JWT issuer validation is intentionally disabled in this blueprint, per current requirement. Audience, signing key, and lifetime validation remain enabled.

---

## HTTP Tests

Use:

```text
tests/LoanProposal.Api.http
```

The file logs in against TenantRegistration and then calls LoanProposal APIs with the issued bearer token.

---

## Production Checklist

- [ ] Replace `EnsureCreatedAsync()` with migrations per service and per tenant database.
- [ ] Replace the development internal API key with mTLS, service identity, or signed internal tokens.
- [ ] Store tenant DB connection strings as secret references rather than plaintext.
- [ ] Move platform entities out of `LoanProposal.Core` into a dedicated TenantRegistration domain package if enforcing repository-level service independence.
- [ ] Add a proper OIDC flow for browser SSO instead of the local LoanProposal login bridge.
- [ ] Add audit logging for tenant/user/workspace/rule/workflow changes.
- [ ] Add integration tests that start both services and prove cross-tenant isolation.
