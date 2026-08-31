---
description: "Protect audit, job telemetry, and data-integrity semantics in synchronization jobs."
applyTo: "src/XtremeIdiots.Portal.Sync.App/*Sync.cs,src/XtremeIdiots.Portal.Sync.App/*Monitor.cs,src/XtremeIdiots.Portal.Sync.App/MapRotations/*Cleanup.cs,src/XtremeIdiots.Portal.Sync.App.Tests/Functions/*SyncTests.cs,src/XtremeIdiots.Portal.Sync.App.Tests/Functions/*MonitorTests.cs,src/XtremeIdiots.Portal.Sync.App.Tests/Functions/*CleanupTests.cs"
---

# Synchronization auditing

- Emit durable audits only after a persisted external or portal state change succeeds; scans, skips, retries, and failed attempts belong in job telemetry or logs.
- Preserve `IJobTelemetry.ExecuteAsync()` around scheduled work so start, success, and failure lifecycle remains observable.
- Retried or manually invoked jobs must not create misleading duplicate audit records.
- For forum claims, audit only a changed system-generated claim set and never treat preserved manual claims as sync-owned.
- Update the affected job tests when changing audit conditions, ordering, idempotency, or failure behavior.
