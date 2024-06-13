data "azurerm_api_management" "platform" {
  provider = azurerm.api_management

  name                = var.legacy_api_management_name
  resource_group_name = var.legacy_api_management_resource_group_name
}
