# Scheduled Job Telemetry

This document describes the custom telemetry events emitted by scheduled jobs in the Portal Sync Function App.

## Overview

All scheduled jobs in the Portal Sync Function App emit structured telemetry events to Application Insights. These events enable monitoring and alerting on job execution status and timing.

## Telemetry Events

Each scheduled job emits the following events:

### Job Started Event
- **Event Name**: `{JobName}_Started`
- **Properties**:
  - `JobName`: Name of the scheduled job
  - `StartTime`: UTC timestamp when the job started (ISO 8601 format)

### Job Completed Event
- **Event Name**: `{JobName}_Completed`
- **Properties**:
  - `JobName`: Name of the scheduled job
  - `StartTime`: UTC timestamp when the job started (ISO 8601 format)
  - `Status`: "Success"
  - `DurationMs`: Duration of the job execution in milliseconds
  - `EndTime`: UTC timestamp when the job completed (ISO 8601 format)
- **Metric**: `{JobName}_Duration` - Duration in milliseconds

### Job Failed Event
- **Event Name**: `{JobName}_Failed`
- **Properties**:
  - `JobName`: Name of the scheduled job
  - `StartTime`: UTC timestamp when the job started (ISO 8601 format)
  - `Status`: "Failed"
  - `DurationMs`: Duration of the job execution in milliseconds
  - `EndTime`: UTC timestamp when the job completed (ISO 8601 format)
  - `ExceptionType`: Type of exception that occurred
  - `ExceptionMessage`: Exception message
- **Exception Tracking**: The exception is also tracked with `TrackException` for detailed error analysis

## Scheduled Jobs

The following scheduled jobs are instrumented with telemetry:

| Job Name | Schedule | Description |
|----------|----------|-------------|
| `ImportLatestBanFiles` | Every 5 minutes (`0 */5 * * * *`) | Imports ban files from game servers |
| `GenerateLatestBansFile` | Every 10 minutes (`0 */10 * * * *`) | Generates consolidated ban files for CoD2, CoD4, and CoD5 |
| `RunMapRedirectSync` | Daily at midnight (`0 0 0 * * *`) | Syncs map redirect entries for CoD4 and CoD5 |
| `RunUserProfileForumsSync` | Every 4 hours (`0 0 */4 * * *`) | Syncs user profiles from forums |
| `RunMapImageSync` | Weekly on Wednesday at midnight (`0 0 0 * * 3`) | Syncs map images from GameTracker |
| `RunRedirectToGameServerMapSync` | Daily at midnight (`0 0 0 * * *`) | Syncs maps to game servers |

## Alerting

These telemetry events enable the following alerting scenarios:

### Job Failure Alerts
Alert when a scheduled job fails by monitoring for `{JobName}_Failed` events.

**Example Kusto Query**:
```kusto
customEvents
| where name endswith "_Failed"
| where timestamp > ago(1h)
| project timestamp, name, tostring(customDimensions.JobName), tostring(customDimensions.ExceptionType), tostring(customDimensions.ExceptionMessage)
```

### Job Not Running Alerts
Alert when a scheduled job hasn't run within its expected timeframe by checking for absence of `{JobName}_Completed` events.

**Example Kusto Query** (for jobs that should run every 5 minutes):
```kusto
let expectedJobName = "ImportLatestBanFiles";
let expectedIntervalMinutes = 10; // Alert if not seen in 2x the expected interval
customEvents
| where name == strcat(expectedJobName, "_Completed")
| where timestamp > ago(24h)
| summarize LastRun = max(timestamp) by tostring(customDimensions.JobName)
| where LastRun < ago(expectedIntervalMinutes * 1m)
```

**Example Kusto Query** (for daily jobs):
```kusto
let expectedJobName = "RunMapRedirectSync";
let expectedIntervalHours = 26; // Alert if not seen in 26 hours (allowing for some delay)
customEvents
| where name == strcat(expectedJobName, "_Completed")
| where timestamp > ago(48h)
| summarize LastRun = max(timestamp) by tostring(customDimensions.JobName)
| where LastRun < ago(expectedIntervalHours * 1h)
```

### Job Duration Alerts
Alert when a job takes longer than expected using the `{JobName}_Duration` metric.

**Example Kusto Query**:
```kusto
customMetrics
| where name endswith "_Duration"
| where value > 60000 // Alert if duration exceeds 60 seconds
| project timestamp, name, value, tostring(customDimensions.JobName)
```

## Implementation

The telemetry tracking is implemented in the `ScheduledJobTelemetry` helper class located at:
`src/XtremeIdiots.Portal.Sync.App/Telemetry/ScheduledJobTelemetry.cs`

Jobs are wrapped with telemetry tracking using the static helper method:
```csharp
await ScheduledJobTelemetry.ExecuteWithTelemetry(
    telemetryClient,
    nameof(JobName),
    async () => { /* job logic */ }
);
```

## Terraform Integration

Alerts based on these telemetry events are configured in Terraform using the `platform-monitoring` remote state reference. The alerts trigger with informational severity and are deployed as part of the infrastructure.

### Deployed Alerts

The following alerts are configured in `terraform/scheduled_job_alerts.tf`:

1. **scheduled_job_failures**: Monitors for any job failure events (`{JobName}_Failed`)
   - Evaluation frequency: Every 5 minutes
   - Window duration: 5 minutes
   - Action: Informational severity alert

2. **import_ban_files_not_running**: Monitors ImportLatestBanFiles execution
   - Expected interval: Every 5 minutes
   - Alert threshold: No completion in 10 minutes
   - Evaluation frequency: Every 5 minutes

3. **generate_ban_files_not_running**: Monitors GenerateLatestBansFile execution
   - Expected interval: Every 10 minutes
   - Alert threshold: No completion in 20 minutes
   - Evaluation frequency: Every 10 minutes

4. **map_redirect_sync_not_running**: Monitors RunMapRedirectSync execution
   - Expected interval: Daily
   - Alert threshold: No completion in 26 hours
   - Evaluation frequency: Every 1 hour

5. **user_profile_sync_not_running**: Monitors RunUserProfileForumsSync execution
   - Expected interval: Every 4 hours
   - Alert threshold: No completion in 5 hours
   - Evaluation frequency: Every 1 hour

6. **map_image_sync_not_running**: DISABLED
   - This alert is disabled due to Azure Monitor window_duration limitations (max P2D/2 days)
   - Weekly jobs cannot be effectively monitored with the 2-day window constraint
   - Job failures will still be caught by the scheduled_job_failures alert
   - Note: Weekly job runs on Wednesdays; a 2-day window would cause false positives

7. **redirect_to_server_sync_not_running**: Monitors RunRedirectToGameServerMapSync execution
   - Expected interval: Daily
   - Alert threshold: No completion in 26 hours
   - Evaluation frequency: Every 1 hour

All active alerts use the informational action group from the `platform-monitoring` remote state reference.
