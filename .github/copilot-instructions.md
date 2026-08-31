# Copilot instructions

Make focused changes within this repository and follow existing Azure Functions, synchronization, test, and Terraform patterns.

- Preserve job scheduling, timer/manual trigger parity, idempotency, pagination, and failure semantics.
- Forum sync may replace system-generated claims only; retain all manually owned claims and never generate `AdditionalPermission`.
- Keep external integrations behind existing clients and repositories, with job telemetry and actionable warning/error logs.
- Audit only successful durable changes, not routine scans or skipped work.
- Use managed identity/OIDC configuration; never add credentials or client secrets.
- Treat Terraform backend, remote-state, provider, output, identity, alert, and deployment wiring as compatibility-sensitive.

Repository structure, commands, and material data-integrity constraints are documented in `AGENTS.md`.
