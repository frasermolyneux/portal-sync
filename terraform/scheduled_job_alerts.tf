# Scheduled Job Monitoring Alerts
# These alerts monitor the custom telemetry events emitted by scheduled jobs
# to detect failures and missed executions.

locals {
  process_health_policy = jsondecode(file("${path.module}/../src/contract/process-health-policy.json"))
  process_health_bindings = join(", ", [
    for binding in local.process_health_policy.bindings :
    "\"${binding.siteId}\", \"${binding.componentId}\", \"${binding.checkId}\", \"${binding.role}\""
  ])
  process_health_jobs = join(", ", [
    for job in local.process_health_policy.jobs :
    "\"${job.name}\", ${job.maximumAgeSeconds}s"
  ])
  sync_job_completions = <<-QUERY
    customEvents
    | where name == "Audit:JobCompleted"
    | where cloud_RoleName in~ ("Sync FuncApp", "${azurerm_linux_function_app.function_app.name}")
    | extend JobName = tostring(customDimensions["Audit.SourceComponent"])
  QUERY
}

# Alert when any scheduled job fails
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "scheduled_job_failures" {
  name                = "${var.workload_name}-${var.environment}-scheduled-job-failures"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes                    = [data.azurerm_application_insights.app_insights.id]
  severity                  = 4 # Informational
  description               = "Alert on scheduled job failures or confirmed fleet/process degradation using the strategic process-health contract"
  evaluation_frequency      = "PT5M"
  window_duration           = "PT5M"
  query_time_range_override = "PT15M"

  criteria {
    query = <<-QUERY
      let bindings = datatable(SiteId:string, ComponentId:string, CheckId:string, Role:string) [${local.process_health_bindings}];
      let failures = customEvents
        | where name == "Audit:JobFailed" and timestamp > ago(5m)
        | where cloud_RoleName in~ ("Sync FuncApp", "${azurerm_linux_function_app.function_app.name}")
        | project timestamp, JobName=tostring(customDimensions["Audit.SourceComponent"]);
      let processes = customEvents
        | where name == "ProcessHealthSnapshot" and timestamp > ago(15m)
        | where tostring(customDimensions.siteId) == "xi" and tostring(customDimensions.contractVersion) == "1"
        | extend SiteId=tostring(customDimensions.siteId), ComponentId=tostring(customDimensions.componentId),
            CheckId=tostring(customDimensions.checkId), Role=cloud_RoleName
        | join kind=inner bindings on SiteId, ComponentId, CheckId, Role
        | summarize arg_max(timestamp, *) by CheckId
        | where tostring(customDimensions.status) in ("degraded", "outage")
        | project timestamp, JobName=CheckId;
      union failures, processes
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

# Preserve the retired import rule's identity and price; monitor current jobs instead.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "import_ban_files_not_running" {
  name                = "${var.workload_name}-${var.environment}-import-ban-files-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes               = [data.azurerm_application_insights.app_insights.id]
  severity             = 4 # Informational
  description          = "Alert on missing process feeds or overdue/unobserved scheduled jobs, including weekly jobs. Retains the legacy import-rule name and price."
  evaluation_frequency = "PT5M"
  window_duration      = "PT2H"

  criteria {
    query = <<-QUERY
      let bindings = datatable(SiteId:string, ComponentId:string, CheckId:string, Role:string) [${local.process_health_bindings}];
      let expectedJobs = datatable(JobName:string, MaximumAge:timespan) [${local.process_health_jobs}];
      let snapshots = customEvents
        | where name == "ProcessHealthSnapshot" and timestamp > ago(15m)
        | where tostring(customDimensions.siteId) == "xi" and tostring(customDimensions.contractVersion) == "1"
        | extend SiteId=tostring(customDimensions.siteId), ComponentId=tostring(customDimensions.componentId),
            CheckId=tostring(customDimensions.checkId), Role=cloud_RoleName
        | summarize LastSeen=max(timestamp) by SiteId, ComponentId, CheckId, Role;
      let missingFeeds = bindings
        | join kind=leftouter snapshots on SiteId, ComponentId, CheckId, Role
        | where isnull(LastSeen)
        | project JobName=CheckId;
      let jobSnapshots = customEvents
        | where name == "ProcessJobSnapshot" and timestamp > ago(15m)
        | where cloud_RoleName == "Sync FuncApp"
        | where tostring(customDimensions.siteId) == "xi" and tostring(customDimensions.contractVersion) == "1"
        | extend JobName=tostring(customDimensions.jobName), LastSuccess=todatetime(customDimensions.lastSuccess)
        | summarize arg_max(timestamp, LastSuccess) by JobName;
      let overdueJobs = expectedJobs
        | join kind=leftouter jobSnapshots on JobName
        | where isnull(LastSuccess) or now() - LastSuccess > MaximumAge
        | project JobName;
      union missingFeeds, overdueJobs
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

  scopes               = [data.azurerm_application_insights.app_insights.id]
  severity             = 4 # Informational
  description          = "Alert when GenerateLatestBansFile job hasn't run in the expected timeframe (20 minutes)"
  evaluation_frequency = "PT10M"
  window_duration      = "PT30M"

  criteria {
    query = <<-QUERY
      ${local.sync_job_completions}
      | where timestamp > ago(30m) and JobName == "GenerateLatestBansFile"
      | summarize LastSuccess=max(timestamp)
      | where isnull(LastSuccess) or now() - LastSuccess > 20m
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

  scopes               = [data.azurerm_application_insights.app_insights.id]
  severity             = 4 # Informational
  description          = "Alert when RunMapRedirectSync job hasn't run in the expected timeframe (26 hours)"
  evaluation_frequency = "PT1H"
  window_duration      = "P2D"

  criteria {
    query = <<-QUERY
      ${local.sync_job_completions}
      | where timestamp > ago(2d) and JobName == "RunMapRedirectSync"
      | summarize LastSuccess=max(timestamp)
      | where isnull(LastSuccess) or now() - LastSuccess > 26h
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

# Alert when RunUserProfileForumsSync hasn't run (expected hourly)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "user_profile_sync_not_running" {
  name                = "${var.workload_name}-${var.environment}-user-profile-sync-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes               = [data.azurerm_application_insights.app_insights.id]
  severity             = 4 # Informational
  description          = "Alert when the hourly RunUserProfileForumsSync job has not completed successfully for two hours"
  evaluation_frequency = "PT1H"
  window_duration      = "PT6H"

  criteria {
    query = <<-QUERY
      ${local.sync_job_completions}
      | where timestamp > ago(6h) and JobName == "RunUserProfileForumsSync"
      | summarize LastSuccess=max(timestamp)
      | where isnull(LastSuccess) or now() - LastSuccess > 2h
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
# NOTE: This alert is disabled because Azure Monitor's window_duration is limited to P2D (2 days),
# which is insufficient for monitoring a weekly job that runs every 7 days. The 2-day window
# would cause false positives on most days of the week when the job hasn't run recently but
# is still within its expected schedule. Job failures will still be caught by the
# scheduled_job_failures alert above.
# resource "azurerm_monitor_scheduled_query_rules_alert_v2" "map_image_sync_not_running" {
#   name                = "${var.workload_name}-${var.environment}-map-image-sync-not-running"
#   resource_group_name = data.azurerm_resource_group.rg.name
#   location            = data.azurerm_resource_group.rg.location
# 
#   scopes              = [data.azurerm_application_insights.app_insights.id]
#   severity            = 4 # Informational
#   description         = "Alert when RunMapImageSync job hasn't completed in the last 2 days (weekly job monitoring)"
#   evaluation_frequency = "PT6H"
#   window_duration      = "P2D"
# 
#   criteria {
#     query = <<-QUERY
#       customEvents
#       | where name == "RunMapImageSync_Completed"
#       | where timestamp > ago(2d)
#       | count
#     QUERY
# 
#     time_aggregation_method = "Count"
#     threshold               = 0
#     operator                = "Equal"
# 
#     failing_periods {
#       minimum_failing_periods_to_trigger_alert = 1
#       number_of_evaluation_periods             = 1
#     }
#   }
# 
#   action {
#     action_groups = [local.action_group_map.informational.id]
#   }
# 
#   auto_mitigation_enabled = true
#   tags                    = var.tags
# }

# Alert when RunRedirectToGameServerMapSync hasn't run (expected daily)
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "redirect_to_server_sync_not_running" {
  name                = "${var.workload_name}-${var.environment}-redirect-to-server-sync-not-running"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  scopes               = [data.azurerm_application_insights.app_insights.id]
  severity             = 4 # Informational
  description          = "Alert when RunRedirectToGameServerMapSync job hasn't run in the expected timeframe (26 hours)"
  evaluation_frequency = "PT1H"
  window_duration      = "P2D"

  criteria {
    query = <<-QUERY
      ${local.sync_job_completions}
      | where timestamp > ago(2d) and JobName == "RunRedirectToGameServerMapSync"
      | summarize LastSuccess=max(timestamp)
      | where isnull(LastSuccess) or now() - LastSuccess > 26h
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
