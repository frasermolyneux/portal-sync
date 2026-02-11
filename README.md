# XtremeIdiots Portal - Sync
[![Build and Test](https://github.com/frasermolyneux/portal-sync/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/build-and-test.yml)
[![Code Quality](https://github.com/frasermolyneux/portal-sync/actions/workflows/codequality.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/codequality.yml)
[![Copilot Setup Steps](https://github.com/frasermolyneux/portal-sync/actions/workflows/copilot-setup-steps.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/copilot-setup-steps.yml)
[![Dependabot Automerge](https://github.com/frasermolyneux/portal-sync/actions/workflows/dependabot-automerge.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/dependabot-automerge.yml)
[![Deploy Dev](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-dev.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-dev.yml)
[![Deploy Prd](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-prd.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/deploy-prd.yml)
[![Destroy Development](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-development.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-development.yml)
[![Destroy Environment](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-environment.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/destroy-environment.yml)
[![PR Verify](https://github.com/frasermolyneux/portal-sync/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/frasermolyneux/portal-sync/actions/workflows/pr-verify.yml)

## Documentation
* [Development Workflows](/docs/development-workflows.md) - Branch strategy, CI/CD triggers, and deployment flows
* [Scheduled Job Telemetry](/docs/SCHEDULED_JOB_TELEMETRY.md) - Telemetry and dashboard signals emitted by scheduled sync jobs.

## Overview
Azure Functions app that synchronizes portal data with external game telemetry and platform services. Handles scheduled and on-demand sync pipelines, ensuring DTO contracts stay aligned with portal APIs and repository data. Integrates with Azure Storage and Application Insights for durable execution and observability. CI/CD uses OIDC-authenticated GitHub Actions and Terraform-provisioned App Service and supporting resources.

## Contributing
Please read the [contributing](CONTRIBUTING.md) guidance; this is a learning and development project.

## Security
Please read the [security](SECURITY.md) guidance; I am always open to security feedback through email or opening an issue.
