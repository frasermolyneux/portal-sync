# AGENTS.md — portal-sync

Azure Functions app (v4, .NET 9 isolated worker) that synchronizes XtremeIdiots portal data with external game telemetry, forum, and platform services. Runs scheduled and on-demand pipelines for ban-file sync, map images, map redirects, and **user-profile forum data** (forum group → portal role-claim mapping).

This file is the brief for the **GitHub Copilot coding agent** (and any other agent that follows the [agents.md](https://agents.md) convention) when it runs in a cloud runner without the local VS Code multi-root workspace context.

> If you are a human reading this in VS Code, prefer `.github/copilot-instructions.md` for project orientation. `AGENTS.md` is the agent execution brief.

---

## Required reading (read these BEFORE doing any work)

The `copilot-setup-steps.yml` workflow checks out `frasermolyneux/.github-copilot` at `./.github-copilot/` in the runner, so the paths below resolve.

1. `.github/copilot-instructions.md` — repo-specific orientation, conventions, **forum-group → claim mapping rules**
2. `.github-copilot/.github/instructions/personal.working-preferences.instructions.md`
3. `.github-copilot/.github/copilot-instructions.md` — org-wide catalog
4. Stack-specific files — see **Stack guardrails** below

---

## Stack guardrails

### Tenant facts (always-on)
- `tenant.subscriptions`, `tenant.regions`, `tenant.identity`

### Enforceable standards
- `standards.oidc-and-secrets` — **no client secrets**
- `standards.dotnet-project`
- `standards.azure-naming`, `standards.azure-tagging`, `standards.terraform-style`
- `standards.branching-and-prs`

### Patterns
- `patterns.api-client` — consumes Portal Repository + Servers Integration + GeoLocation + Invision clients
- `patterns.nbgv-versioning`
- `patterns.terraform-remote-state`

### Platform consumption contracts
- `platform.workloads`, `platform.monitoring`, `platform.hosting`

### Shared
- `shared.api-client-abstractions`
- `shared.observability-appinsights` — `IJobTelemetry.ExecuteAsync()` wraps every scheduled job
- `shared.invision-api-client` — typed forum API client

---

## Build, test, format

```pwsh
dotnet build src/XtremeIdiots.Portal.Sync.App.sln
dotnet test src --filter "FullyQualifiedName!~IntegrationTests"
dotnet format src/XtremeIdiots.Portal.Sync.App.sln --verify-no-changes

terraform -chdir=terraform fmt -check -recursive
terraform -chdir=terraform init -backend-config=backends/dev.backend.hcl
terraform -chdir=terraform validate
terraform -chdir=terraform plan -var-file=tfvars/dev.tfvars
```

---

## Do NOT

- ❌ Do not `git commit`, `git push`, force-push, rebase, or branch-mutate. Work on the assigned branch only.
- ❌ Do not introduce client secrets / forum API keys in code. Settings come from App Configuration + Key Vault via managed identity.
- ❌ Do not bypass `dotnet format`, `dotnet test`, `terraform fmt`, or `terraform validate`.
- ❌ Do not wrap a new scheduled job without `IJobTelemetry.ExecuteAsync()` — observability is required.
- ❌ Do not add a timer trigger without a paired HTTP trigger for manual execution.
- ❌ **Do not overwrite manually-assigned `AdditionalPermission` claims during forum sync** — the sync flow preserves them by separating system-generated from manual claims and merging back. Read `UserProfileForumsSync` before changing claim-sync logic.
- ❌ Do not generate `AdditionalPermission` claim types from portal-sync — those are manually assigned via the portal UI. This repo only emits the system claims listed in `.github/copilot-instructions.md`.
- ❌ Do not add new forum group → role mappings without a corresponding change in the portal-web authorization model.
- ❌ Do not modify `.github/workflows/`, `.github/dependabot.yml`, or `version.json` unless that is the explicit task.

---

## Validation before opening PR

- [ ] `dotnet build` succeeds (clean)
- [ ] `dotnet test --filter "FullyQualifiedName!~IntegrationTests"` passes
- [ ] `dotnet format --verify-no-changes` passes
- [ ] `terraform fmt -check -recursive` passes
- [ ] `terraform validate` + `terraform plan -var-file=tfvars/dev.tfvars` succeed
- [ ] Each new scheduled job uses `IJobTelemetry.ExecuteAsync()`
- [ ] Each new timer trigger has a paired HTTP trigger
- [ ] Claim-sync changes preserve manually-assigned `AdditionalPermission` claims
- [ ] No new secrets / GUIDs / connection strings
- [ ] PR body cites each acceptance criterion
- [ ] Risk/rollout section filled in

---

## Escalation

If you hit any of the conditions below, **open the PR as draft** and **apply the `needs-decision` label** instead of pushing forward to ready-for-review. Post a comment on the originating issue summarising what's blocking you and what decision is needed.

Stop and escalate when:

- The change would force-overwrite manually-assigned `AdditionalPermission` claims.
- A new forum group → role mapping requires coordinated portal-web auth changes.
- A `code-review` finding is **High** and cannot be resolved in-scope.
- An external API contract (Invision, GameTracker, map redirect) has changed and the client needs versioning.
