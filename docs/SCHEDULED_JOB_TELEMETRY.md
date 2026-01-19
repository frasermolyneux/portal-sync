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
| `RunUserProfileForumsSync` | Daily at midnight (`0 0 0 * * *`) | Syncs user profiles from forums |
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

Alerts based on these telemetry events should be configured in Terraform using the `platform-monitoring` remote state reference. The alerts should trigger with informational severity as specified in the requirements.

Example alert configuration structure (to be implemented in Terraform):
```hcl
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "job_failure_alert" {
  name                = "sync-job-failure-alert"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location
  
  scopes              = [local.app_insights.id]
  severity            = 4 # Informational
  
  criteria {
    query = <<-QUERY
      customEvents
      | where name endswith "_Failed"
      | where timestamp > ago(5m)
    QUERY
    
    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"
  }
  
  action {
    action_groups = [local.action_group_map.informational.id]
  }
}
```
