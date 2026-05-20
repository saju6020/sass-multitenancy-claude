# SaaS Multitenancy Architecture

This blueprint uses a split-service SaaS model:

- TenantRegistration.API is the platform control plane.
- LoanProposal.API is the tenant-aware business data plane.
- Tenant identity is centralized in the platform database.
- Business data is isolated by physical database per tenant.

## 1. Tenant Registration / Control Plane

```mermaid
flowchart TB
    PlatformAdmin[Platform Admin]

    subgraph ControlPlane["TenantRegistration.API - SaaS Control Plane"]
        TenantUi["/platform/tenants MVC UI"]
        TenantApi["/api/platform/tenants"]
        Auth["/auth/token and /account/login"]
        Registry["Internal tenant registry endpoints"]
        Provisioner["LoanProposal tenant DB provisioner"]
    end

    subgraph PlatformDb["Platform DB: tenantregistration_platform"]
        Tenants["Tenants\nid, name, slug, currency, timezone, status"]
        Users["PlatformUsers\ntenant_id, tenant_slug, email, password_hash, roles"]
        Metadata["Service metadata\nLoanProposal database name and connection string"]
    end

    subgraph TenantDatabases["LoanProposal Tenant Databases"]
        DbA["loanproposal_acme_bank"]
        DbB["loanproposal_global_finance"]
        DbC["loanproposal_test_tenant_one"]
    end

    PlatformAdmin -->|"Create tenant + TenantAdmin credentials"| TenantUi
    PlatformAdmin -->|"Optional API registration"| TenantApi
    TenantUi --> Tenants
    TenantUi --> Users
    TenantUi --> Metadata
    TenantApi --> Tenants
    TenantApi --> Users
    TenantApi --> Metadata
    TenantUi -->|"EnsureCreatedAsync"| Provisioner
    TenantApi -->|"EnsureCreatedAsync"| Provisioner
    Provisioner --> DbA
    Provisioner --> DbB
    Provisioner --> DbC
    Auth -->|"Issue JWT with tenant_id, tenant_slug, roles"| PlatformAdmin
    Registry -->|"Returns tenant service descriptor"| Metadata
```

### Control Plane Responsibilities

- Registers tenants.
- Creates the first `TenantAdmin` user during tenant creation.
- Stores tenant identity and service metadata.
- Issues JWTs containing tenant and role claims.
- Exposes internal registry endpoints so business services can resolve the correct tenant database.
- Provisions the LoanProposal tenant database when a new tenant is registered.

## 2. LoanProposal Business API / Data Plane

```mermaid
flowchart TB
    User["Tenant user\nTenantAdmin / LoanOfficer / Reviewer / Approver"]

    subgraph BusinessPlane["LoanProposal.API - Tenant-Aware Business Service"]
        LoginBridge["/account/login\nlocal sign-in bridge"]
        Middleware["TenantResolutionMiddleware"]
        Dashboard["Dashboard"]
        Config["Tenant configuration\nsettings, custom fields, rules, workflows"]
        Loans["Loan applications\ncreate, review, approve, reject"]
        AutoProvision["Tenant DB auto-provision\nEnsureCreatedAsync on first access"]
    end

    subgraph RegistryService["TenantRegistration.API"]
        Token["/auth/token"]
        InternalRegistry["/internal/tenants/{id}/services/loan-proposal"]
    end

    subgraph IsolatedTenantData["Database-per-tenant isolation"]
        TenantA["Tenant A DB\nloanproposal_acme_bank"]
        TenantB["Tenant B DB\nloanproposal_global_finance"]
        TenantC["Tenant C DB\nloanproposal_test_tenant_one"]
    end

    User -->|"Login credentials"| LoginBridge
    LoginBridge -->|"Validate credentials"| Token
    Token -->|"JWT: tenant_id, tenant_slug, roles"| LoginBridge
    User -->|"Authenticated request"| Middleware
    Middleware -->|"Resolve tenant_id / tenant_slug"| InternalRegistry
    InternalRegistry -->|"Connection string + active status"| Middleware
    Middleware --> AutoProvision
    AutoProvision --> TenantA
    AutoProvision --> TenantB
    AutoProvision --> TenantC
    Middleware --> Dashboard
    Middleware --> Config
    Middleware --> Loans
    Config -->|"Selected tenant DB only"| TenantA
    Config -->|"Selected tenant DB only"| TenantB
    Config -->|"Selected tenant DB only"| TenantC
    Loans -->|"Selected tenant DB only"| TenantA
    Loans -->|"Selected tenant DB only"| TenantB
    Loans -->|"Selected tenant DB only"| TenantC
```

### Business Plane Responsibilities

- Delegates authentication to TenantRegistration.
- Reads tenant claims from JWT or local cookie.
- Resolves tenant database metadata from TenantRegistration.
- Enforces role-based access.
- Opens only the resolved tenant database for workspace configuration and loan proposal workflows.
- Auto-creates the tenant database on first access if metadata exists but the physical database is missing.

## SaaS Multitenancy Modality

```mermaid
flowchart LR
    subgraph SharedPlatform["Shared Platform Layer"]
        TR["TenantRegistration.API"]
        PlatformDb["tenantregistration_platform"]
    end

    subgraph SharedBusinessApp["Shared Business Application Layer"]
        LP["LoanProposal.API"]
        SharedCode["Shared app code, controllers, rules, workflows engine"]
    end

    subgraph IsolatedData["Isolated Data Layer"]
        A["Tenant A database"]
        B["Tenant B database"]
        C["Tenant C database"]
    end

    TR --> PlatformDb
    LP --> SharedCode
    LP -->|"tenant_id selects connection"| A
    LP -->|"tenant_id selects connection"| B
    LP -->|"tenant_id selects connection"| C
```

This is a **shared application, database-per-tenant** SaaS model:

- Application instances are shared across tenants.
- Tenant identity and login are centralized.
- Business data is isolated in separate physical databases.
- The tenant registry maps each tenant to its business service database.
- Tenant claims and role claims decide which tenant and features a user can access.
