resource "azurerm_monitor_activity_log_alert" "legacy_rg_resource_health" {
  name = "portal-sync-${var.environment} - ${azurerm_resource_group.legacy_rg.name} - resource health"

  resource_group_name = azurerm_resource_group.legacy_rg.name
  location            = "global"

  scopes      = [azurerm_resource_group.legacy_rg.id]
  description = "Resource health alert for ${azurerm_resource_group.legacy_rg.name} resource group"

  criteria {
    category = "ResourceHealth"

    resource_health {
      previous = ["Available"]
    }
  }

  action {
    action_group_id = var.environment == "prd" ? data.azurerm_monitor_action_group.critical.id : data.azurerm_monitor_action_group.informational.id
  }

  tags = var.tags
}
