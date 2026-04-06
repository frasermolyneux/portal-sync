resource "azurerm_api_management_product" "api_product" {
  product_id = local.sync_api.api_management.root_path

  resource_group_name = data.azurerm_api_management.api_management.resource_group_name
  api_management_name = data.azurerm_api_management.api_management.name

  display_name = "Sync API"

  subscription_required = false
  approval_required     = false
  published             = true
}
