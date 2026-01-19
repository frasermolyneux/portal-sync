# Scheduled Job Monitoring Alerts
# These alerts monitor the custom telemetry events emitted by scheduled jobs
# to detect failures and missed executions.

# Alert when any scheduled job fails
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "scheduled_job_failures" {
  name                = "${var.workload_name}-${var.environment}-scheduled-job-failures"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when any scheduled job fails in the Portal Sync Function App"
  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"

  criteria {
    query = <<-QUERY
      customEvents
      | where name endswith "_Failed"
      | where timestamp > ago(5m)
      | project timestamp, name, tostring(customDimensions.JobName), tostring(customDimensions.ExceptionType), tostring(customDimensions.ExceptionMessage)
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}

# Alert when ImportLatestBanFiles hasn't run (expected every 5 minutes)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "import_ban_files_not_running" {
  name                = "${var.workload_name}-${var.environment}-import-ban-files-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when ImportLatestBanFiles job hasn't run in the expected timeframe (10 minutes)"
  evaluation_frequency = "PT5M"
  window_duration      = "PT15M"

  criteria {
    query = <<-QUERY
      let expectedJobName = "ImportLatestBanFiles";
      let expectedIntervalMinutes = 10; // Alert if not seen in 2x the expected interval
      customEvents
      | where name == strcat(expectedJobName, "_Completed")
      | where timestamp > ago(15m)
      | summarize LastRun = max(timestamp)
      | extend MinutesSinceLastRun = datetime_diff('minute', now(), LastRun)
      | where MinutesSinceLastRun > expectedIntervalMinutes
      | project LastRun, MinutesSinceLastRun
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}

# Alert when GenerateLatestBansFile hasn't run (expected every 10 minutes)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "generate_ban_files_not_running" {
  name                = "${var.workload_name}-${var.environment}-generate-ban-files-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when GenerateLatestBansFile job hasn't run in the expected timeframe (20 minutes)"
  evaluation_frequency = "PT10M"
  window_duration      = "PT30M"

  criteria {
    query = <<-QUERY
      let expectedJobName = "GenerateLatestBansFile";
      let expectedIntervalMinutes = 20; // Alert if not seen in 2x the expected interval
      customEvents
      | where name == strcat(expectedJobName, "_Completed")
      | where timestamp > ago(30m)
      | summarize LastRun = max(timestamp)
      | extend MinutesSinceLastRun = datetime_diff('minute', now(), LastRun)
      | where MinutesSinceLastRun > expectedIntervalMinutes
      | project LastRun, MinutesSinceLastRun
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}

# Alert when RunMapRedirectSync hasn't run (expected daily)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "map_redirect_sync_not_running" {
  name                = "${var.workload_name}-${var.environment}-map-redirect-sync-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when RunMapRedirectSync job hasn't run in the expected timeframe (26 hours)"
  evaluation_frequency = "PT1H"
  window_duration      = "P2D"

  criteria {
    query = <<-QUERY
      let expectedJobName = "RunMapRedirectSync";
      let expectedIntervalHours = 26; // Alert if not seen in 26 hours (allowing for some delay)
      customEvents
      | where name == strcat(expectedJobName, "_Completed")
      | where timestamp > ago(2d)
      | summarize LastRun = max(timestamp)
      | extend HoursSinceLastRun = datetime_diff('hour', now(), LastRun)
      | where HoursSinceLastRun > expectedIntervalHours
      | project LastRun, HoursSinceLastRun
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}

# Alert when RunUserProfileForumsSync hasn't run (expected daily)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "user_profile_sync_not_running" {
  name                = "${var.workload_name}-${var.environment}-user-profile-sync-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when RunUserProfileForumsSync job hasn't run in the expected timeframe (26 hours)"
  evaluation_frequency = "PT1H"
  window_duration      = "P2D"

  criteria {
    query = <<-QUERY
      let expectedJobName = "RunUserProfileForumsSync";
      let expectedIntervalHours = 26; // Alert if not seen in 26 hours (allowing for some delay)
      customEvents
      | where name == strcat(expectedJobName, "_Completed")
      | where timestamp > ago(2d)
      | summarize LastRun = max(timestamp)
      | extend HoursSinceLastRun = datetime_diff('hour', now(), LastRun)
      | where HoursSinceLastRun > expectedIntervalHours
      | project LastRun, HoursSinceLastRun
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}

# Alert when RunMapImageSync hasn't run (expected weekly)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "map_image_sync_not_running" {
  name                = "${var.workload_name}-${var.environment}-map-image-sync-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when RunMapImageSync job hasn't run in the expected timeframe (8 days)"
  evaluation_frequency = "PT6H"
  window_duration      = "P10D"

  criteria {
    query = <<-QUERY
      let expectedJobName = "RunMapImageSync";
      let expectedIntervalDays = 8; // Alert if not seen in 8 days (allowing for some delay)
      customEvents
      | where name == strcat(expectedJobName, "_Completed")
      | where timestamp > ago(10d)
      | summarize LastRun = max(timestamp)
      | extend DaysSinceLastRun = datetime_diff('day', now(), LastRun)
      | where DaysSinceLastRun > expectedIntervalDays
      | project LastRun, DaysSinceLastRun
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}

# Alert when RunRedirectToGameServerMapSync hasn't run (expected daily)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "redirect_to_server_sync_not_running" {
  name                = "${var.workload_name}-${var.environment}-redirect-to-server-sync-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes              = [data.azurerm_application_insights.app_insights.id]
  severity            = 4 # Informational
  description         = "Alert when RunRedirectToGameServerMapSync job hasn't run in the expected timeframe (26 hours)"
  evaluation_frequency = "PT1H"
  window_duration      = "P2D"

  criteria {
    query = <<-QUERY
      let expectedJobName = "RunRedirectToGameServerMapSync";
      let expectedIntervalHours = 26; // Alert if not seen in 26 hours (allowing for some delay)
      customEvents
      | where name == strcat(expectedJobName, "_Completed")
      | where timestamp > ago(2d)
      | summarize LastRun = max(timestamp)
      | extend HoursSinceLastRun = datetime_diff('hour', now(), LastRun)
      | where HoursSinceLastRun > expectedIntervalHours
      | project LastRun, HoursSinceLastRun
    QUERY

    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.informational.id]
  }

  auto_mitigation_enabled = true
  tags                    = var.tags
}
