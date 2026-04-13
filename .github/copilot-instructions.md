# Copilot Instructions for portal-sync

## Project Overview

This repository contains **portal-sync**, an Azure Functions (v4, isolated worker) application that synchronizes XtremeIdiots portal data with external game telemetry, forum, and platform services. It runs scheduled and on-demand sync pipelines for ban files, map images, map redirects, and user profile forum data.

## Technology Stack

- **Runtime:** .NET 9, C# 13, Azure Functions v4 (isolated worker model)
- **Infrastructure:** Terraform (Azure provider) — see `terraform/`
- **CI/CD:** GitHub Actions with OIDC authentication — see `.github/workflows/`
- **Observability:** Application Insights (TelemetryClient, custom `ScheduledJobTelemetry`)
- **Storage:** Azure Blob Storage for ban files and map images
- **External APIs:** Portal Repository API, Servers Integration API, InvisionCommunity forums, GameTracker map images, map redirect service

## Solution Structure

- `src/XtremeIdiots.Portal.Sync.App/` — Main Azure Functions app (timer-triggered and HTTP-triggered sync functions)
- `src/XtremeIdiots.Portal.Forums.Integration/` — Forum integration library (admin action topics, demo manager)
- `terraform/` — Infrastructure-as-code for Function App, Storage, alerts
- `docs/` — Development workflows and telemetry documentation

## Key Conventions

- Use **primary constructor injection** with null-guard fields for all function classes.
- Wrap all scheduled jobs with `ScheduledJobTelemetry.ExecuteWithTelemetry()` for consistent start/success/failure telemetry.
- Every timer-triggered function should have a corresponding manual HTTP-triggered function.
- Use `ConfigureAwait(false)` on all async calls.
- Follow the existing pattern of interfaces in `Interfaces/` and implementations in peer folders.
- Configuration classes live in `Configuration/` and are bound via `IOptions<T>`. Some settings are read directly from `IConfiguration` (e.g., `MapRedirect:BaseUrl`, forum IDs).

## Terraform Conventions

- Resources use `local.*` references defined in `locals.tf`.
- Configuration and secrets are managed centrally via Azure App Configuration (with Key Vault references hosted in the shared `portal-environments` Key Vault).
- Remote state data sources pull outputs from other portal infrastructure repositories.

## Testing and Quality

- Run `dotnet build` from `src/` to verify compilation.
- The `codequality.yml` workflow runs static analysis; `build-and-test.yml` handles build verification.
- There are currently no unit test projects; validate changes through build and code review.

## Forum Group → Claim Mapping

The `UserProfileForumsSync` class synchronizes forum group membership to portal claims. Key details:

### System-Generated Claims (`UserProfileClaimType` constants)

These claims are generated from Invision Community forum member data and group membership:

- **Identity claims** (always generated): `UserProfileId`, `XtremeIdiotsId`, `Email`, `PhotoUrl`, `TimeZone`
- **Role claims** (from forum group names): `SeniorAdmin`, `HeadAdmin`, `GameAdmin`, `Moderator`

Forum group names map to role claims with game-type scoping. For example:
- `"Senior Admin"` → `SeniorAdmin` (global, value `Unknown`)
- `"COD4 Head Admin"` → `HeadAdmin` with value `CallOfDuty4`
- `"COD4 Admin"` → `GameAdmin` with value `CallOfDuty4`
- `"COD4 Moderator"` → `Moderator` with value `CallOfDuty4`

Supported games: COD2, COD4, COD5, Insurgency, Minecraft, ARMA (maps to Arma/Arma2/Arma3), Battlefield (maps to BF1/BF3/BF4/BF5/BFBC2).

> **Note:** portal-sync only generates the system claims listed above. Manually-assigned permissions use `AdditionalPermission` claim types (e.g. `GameServers.Admin.Rcon`, `GameServers.Credentials.Ftp.Read`) and are not managed by portal-sync.

### Additional Permissions Survive Sync

Manually assigned additional permissions (using `AdditionalPermission` claim types like `GameServers.Admin.Rcon`) are **preserved across sync cycles**. The sync process:

1. Fetches all existing claims for a user
2. Separates system-generated claims from manually assigned ones (`SystemGenerated=false`)
3. Regenerates system claims from current forum group membership
4. Merges the preserved manual claims back in (with deduplication)
5. Calls `SetUserProfileClaims()` atomically to replace all claims

This means administrators can safely assign additional permissions via the portal UI knowing they will not be overwritten by the next forum sync.

## Important Patterns

- Ban file sync uses FTP to monitor and push ban files to game servers.
- Map image sync downloads images from GameTracker, detects HTML anti-bot pages, and uploads to blob storage via the Repository API.
- All API clients are registered in `Program.cs` using builder-pattern extension methods with Entra ID (Azure AD) authentication. Configuration is loaded from Azure App Configuration with label-based selectors.
