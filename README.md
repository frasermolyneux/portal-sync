# XtremeIdiots Portal - Sync
[![Build and Test](https://github.com/frasermolyneux/portal-sync/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/build-and-test.yml)
[![Code Quality](https://github.com/frasermolyneux/portal-sync/actions/workflows/codequality.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/codequality.yml)
[![Copilot Setup Steps](https://github.com/frasermolyneux/portal-sync/actions/workflows/copilot-setup-steps.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/copilot-setup-steps.yml)
[![Dependabot Auto-Merge](https://github.com/frasermolyneux/portal-sync/actions/workflows/dependabot-automerge.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/dependabot-automerge.yml)
[![Deploy Dev](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-dev.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-dev.yml)
[![Deploy Prd](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-prd.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-prd.yml)
[![Destroy Development](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-development.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-development.yml)
[![Destroy Environment](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-environment.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-environment.yml)
[![PR Verify](https://github.com/frasermolyneux/portal-sync/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/pr-verify.yml)

## Documentation
* [Development Workflows](/docs/development-workflows.md) - Branch strategy, CI/CD triggers, and deployment flows
* [Scheduled Job Telemetry](/docs/SCHEDULED_JOB_TELEMETRY.md) - Telemetry and dashboard signals emitted by scheduled sync jobs.
* [Function Telemetry](/docs/telemetry.md) - Separate Azure Functions host and isolated-worker telemetry controls.

## Overview
Azure Functions app that synchronizes portal data with external game telemetry and platform services. Handles scheduled and on-demand sync pipelines, ensuring DTO contracts stay aligned with portal APIs and repository data. Integrates with Azure Storage and Application Insights for durable execution and observability. CI/CD uses OIDC-authenticated GitHub Actions and Terraform-provisioned App Service and supporting resources.

## Redirect catalogue integration

Map synchronization uses the shared redirect client with `RedirectApi:BaseUrl`
and `RedirectApi:ApplicationAudience`, loaded from this application's App
Configuration namespace. Its managed identity needs only `Maps.Read`.
The obsolete PHP endpoint and API key are not used.

Each game snapshot pins discovery, map pages and recursive catalogue pages to
one version, validating complete file counts, bytes and authoritative URLs
before repository writes. A catalogue conflict discards the whole attempt;
other API failures fail the job rather than synchronizing partial data.
COD4 and COD4x share one source snapshot. Paging is per catalogue, not per map.
Updates include file versions and explicitly replace even empty file lists;
deleted source folders remove only their own legacy redirect references.

## Contributing
Please read the [contributing](CONTRIBUTING.md) guidance; this is a learning and development project.

## Security
Please read the [security](SECURITY.md) guidance; I am always open to security feedback through email or opening an issue.
