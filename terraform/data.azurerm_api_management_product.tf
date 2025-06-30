data "azurerm_api_management_product" "repository_api_product" {
  product_id = var.repository_api.apim_product_id

  api_management_name = data.azurerm_api_management.core.name
  resource_group_name = data.azurerm_api_management.core.resource_group_name
}
