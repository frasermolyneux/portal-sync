# portal-sync

.NET 10 isolated Azure Functions application that synchronizes portal data with forum, game-server, map, and platform services through scheduled and manual jobs.

## Ownership and paths

- `src/XtremeIdiots.Portal.Sync.App/`: triggers, synchronization jobs, configuration, repositories, and external API clients.
- `src/XtremeIdiots.Portal.Forums.Integration/`: forum-specific integration behavior.
- `src/XtremeIdiots.Portal.Sync.App.Tests/`: job, trigger, claim-mapping, idempotency, and failure-path tests.
- `terraform/`: Function App, storage, Key Vault integration, APIM wiring, identities, and alerts.

## Commands

```pwsh
dotnet build src/XtremeIdiots.Portal.Sync.App.slnx
dotnet test src --filter "FullyQualifiedName!~IntegrationTests"
dotnet format src/XtremeIdiots.Portal.Sync.App.slnx --verify-no-changes
terraform -chdir=terraform fmt -check -recursive
terraform -chdir=terraform init -lockfile=readonly -backend-config=backends/dev.backend.hcl
terraform -chdir=terraform validate
```

## Synchronization and job constraints

- `src/contract/` mirrors platform-monitoring's process-health policy/helper. Keep
  original durable success/failure times, explicit partial failures and Unknown
  for unobserved jobs. The read-only five-minute health projector is not a business
  job and must not recursively wrap itself in job telemetry. Reuse existing host
  storage, compute and alert identities; never make completed business side effects
  retry solely because their telemetry persistence failed.

- Scheduled jobs must run through `IJobTelemetry.ExecuteAsync()` and retain a corresponding HTTP trigger for controlled manual execution.
- Preserve scheduling, pagination, retry/fail-fast, cancellation, and partial-failure semantics. Avoid duplicate side effects when a job is retried or manually re-run.
- Forum synchronization regenerates system-owned identity and role claims from forum membership while preserving every claim where `SystemGenerated == false`. Merge and deduplicate before the atomic `SetUserProfileClaims` replacement.
- `AdditionalPermission` claims are manually owned outside this service. Never generate, discard, or overwrite them. Forum group-to-role mapping changes require coordinated authorization-model changes.
- Keep external forum, Repository API, Servers Integration API, GameTracker, redirect-service, FTP, and blob interactions behind their existing clients/repositories. Preserve anti-bot/non-image detection and cleanup behavior.
- Durable audits represent successful persisted changes, not scans, skips, heartbeats, or failed attempts. Job telemetry and warning/error logs remain the operational failure signal.
- Maintain timer/manual trigger parity and tests for lifecycle, idempotency, preservation, and failure behavior when changing a job.

## Infrastructure and delivery

- Configuration comes from App Configuration and Key Vault through managed identity; do not embed API keys, client secrets, or connection strings.
- Terraform CLI compatibility and provider bounds are defined in `terraform/providers.tf`; exact selections come from the committed root CLI pin and provider lock. Keep the `azurerm` backend. It consumes remote state from platform workloads, platform monitoring, portal environments, and portal core.
- Backend files are under `terraform/backends/`; environment variables are under `terraform/tfvars/`. Preserve backend/state keys, remote-state contracts, provider behavior, Function App settings, identities, schedules, alerts, and APIM outputs.
- Deployments use GitHub environments, OIDC, Terraform, Function App deployment, and APIM synchronization. Commit each root's `.terraform-version` and `.terraform.lock.hcl`. Select the exact root CLI pin before invoking Terraform; normal initialization uses `-lockfile=readonly`. Regenerate provider selections only in a reviewed dependency-update PR; never commit state, plans, or `.terraform/`.

Use the README and focused source tests for operational detail; keep this file centered on invariants that protect synchronized data and job behavior.


CI-only main pushes run change detection but skip cloud/backend deployment jobs. Automatic deployment requires a change to the workload inputs already declared by the deployment filters; scheduled maintenance and explicit workflow dispatch retain their existing behavior. A deployment-workflow-only change requires explicit dispatch to exercise cloud operations.
