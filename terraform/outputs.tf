output "function_app_name" {
  value = azurerm_linux_function_app.function_app.name
}

output "function_app_default_hostname" {
  value = azurerm_linux_function_app.function_app.default_hostname
}

output "resource_group_name" {
  value = data.azurerm_resource_group.rg.name
}

output "api_version_set_id" {
  value = azurerm_api_management_api_version_set.api_version_set.name
}

output "api_management_name" {
  value = data.azurerm_api_management.api_management.name
}

output "api_management_resource_group_name" {
  value = data.azurerm_api_management.api_management.resource_group_name
}

output "api_management_product_id" {
  value = azurerm_api_management_product.api_product.product_id
}
