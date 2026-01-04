resource "azurerm_api_management_subscription" "legacy_repository_api_subscription" {
  api_management_name = data.azurerm_api_management.core.name
  resource_group_name = data.azurerm_api_management.core.resource_group_name

  state         = "active"
  allow_tracing = false

  product_id   = data.azurerm_api_management_product.repository_api_product.id
  display_name = format("%s-%s", local.legacy_function_app_name, data.azurerm_api_management_product.repository_api_product.product_id)
}

resource "azurerm_api_management_subscription" "legacy_servers_integration_api_subscription" {
  api_management_name = data.azurerm_api_management.core.name
  resource_group_name = data.azurerm_api_management.core.resource_group_name

  state         = "active"
  allow_tracing = false

  product_id   = data.azurerm_api_management_product.servers_integration_api_product.id
  display_name = format("%s-%s", local.legacy_function_app_name, data.azurerm_api_management_product.servers_integration_api_product.product_id)
}

moved {
  from = azurerm_api_management_subscription.repository_api_subscription
  to   = azurerm_api_management_subscription.legacy_repository_api_subscription
}

moved {
  from = azurerm_api_management_subscription.servers_integration_api_subscription
  to   = azurerm_api_management_subscription.legacy_servers_integration_api_subscription
}
