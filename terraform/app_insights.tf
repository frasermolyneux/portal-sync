resource "azurerm_application_insights" "ai" {
  name                = local.app_insights_name
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  workspace_id        = "/subscriptions/${var.log_analytics_subscription_id}/resourceGroups/${var.log_analytics_resource_group_name}/providers/Microsoft.OperationalInsights/workspaces/${var.log_analytics_workspace_name}"

  application_type = "web"
}
