data "azurerm_api_management" "core" {
  name                = var.api_management_name
  resource_group_name = data.azurerm_resource_group.core.name
}
