data "azurerm_api_management_api" "servers_integration_api" {
  name                = var.servers_integration_api.apim_api_name
  api_management_name = data.azurerm_api_management.core.name
  resource_group_name = data.azurerm_api_management.core.resource_group_name

  revision = var.servers_integration_api.apim_api_revision
}
