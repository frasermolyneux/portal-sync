resource "azurerm_monitor_metric_alert" "legacy_ftp_dependencies" {
  count = var.environment == "prd" ? 1 : 0

  name = "portal-sync-${var.environment} - FTP Dependencies - failure count"

  resource_group_name = azurerm_resource_group.legacy_rg.name
  scopes              = [data.azurerm_application_insights.core.id]

  description = "FTP dependency failure count for portal-sync"

  frequency   = "PT5M"
  window_size = "PT30M"

  criteria {
    metric_namespace = "microsoft.insights/components"
    metric_name      = "dependencies/count"
    aggregation      = "Count"
    operator         = "GreaterThan"
    threshold        = 0

    dimension {
      name     = "dependency/type"
      operator = "Include"
      values   = ["FTP"]
    }

    dimension {
      name     = "dependency/success"
      operator = "Include"
      values   = ["false"]
    }

    skip_metric_validation = false
  }

  severity = 1

  action {
    action_group_id = data.azurerm_monitor_action_group.high.id
  }

  tags = var.tags
}

moved {
  from = azurerm_monitor_metric_alert.ftp_dependencies
  to   = azurerm_monitor_metric_alert.legacy_ftp_dependencies
}
