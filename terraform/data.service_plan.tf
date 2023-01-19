data "azurerm_service_plan" "plan" {
  provider            = azurerm.web_apps
  name                = var.web_apps_app_service_plan_name
  resource_group_name = var.web_apps_resource_group_name
}
