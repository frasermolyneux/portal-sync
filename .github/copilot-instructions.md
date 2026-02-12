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
- `terraform/` — Infrastructure-as-code for Function App, Key Vault, Storage, alerts
- `docs/` — Development workflows and telemetry documentation

## Key Conventions

- Use **primary constructor injection** with null-guard fields for all function classes.
- Wrap all scheduled jobs with `ScheduledJobTelemetry.ExecuteWithTelemetry()` for consistent start/success/failure telemetry.
- Every timer-triggered function should have a corresponding manual HTTP-triggered function.
- Use `ConfigureAwait(false)` on all async calls.
- Follow the existing pattern of interfaces in `Interfaces/` and implementations in peer folders.
- Configuration classes live in `Configuration/` and are bound via `IOptions<T>`.

## Terraform Conventions

- Resources use `local.*` references defined in `locals.tf`.
- Secrets are stored in Key Vault and referenced via `@Microsoft.KeyVault(...)` app settings.
- Remote state data sources pull outputs from other portal infrastructure repositories.

## Testing and Quality

- Run `dotnet build` from `src/` to verify compilation.
- The `codequality.yml` workflow runs static analysis; `build-and-test.yml` handles build verification.
- There are currently no unit test projects; validate changes through build and code review.

## Important Patterns

- Ban file sync uses FTP to monitor and push ban files to game servers.
- Map image sync downloads images from GameTracker, detects HTML anti-bot pages, and uploads to blob storage via the Repository API.
- All API clients are registered in `Program.cs` using builder-pattern extension methods with Entra ID (Azure AD) authentication.
