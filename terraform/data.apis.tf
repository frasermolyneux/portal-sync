data "azurerm_api_management_api" "repository_api" {
  provider = azurerm.api_management

  name                = "repository-api-v2"
  api_management_name = data.azurerm_api_management.platform.name
  resource_group_name = data.azurerm_api_management.platform.resource_group_name

  revision = "1"
}
