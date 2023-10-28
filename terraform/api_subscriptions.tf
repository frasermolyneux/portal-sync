resource "azurerm_api_management_subscription" "repository_api_subscription" {
  provider = azurerm.api_management

  api_management_name = data.azurerm_api_management.platform.name
  resource_group_name = data.azurerm_api_management.platform.resource_group_name

  state         = "active"
  allow_tracing = false

  api_id       = split(";", data.azurerm_api_management_api.repository_api.id)[0] // Strip revision from id when creating subscription
  display_name = format("%s-%s", local.function_app_name, data.azurerm_api_management_api.repository_api.name)
}
