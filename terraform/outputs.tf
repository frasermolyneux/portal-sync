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

output "ban_files_storage" {
  description = "Storage account hosting the regenerated central ban files (consumed by portal-server-agent for outbound FTP push to game servers)."
  value = {
    id             = azurerm_storage_account.app_data_storage.id
    name           = azurerm_storage_account.app_data_storage.name
    blob_endpoint  = azurerm_storage_account.app_data_storage.primary_blob_endpoint
    container_name = azurerm_storage_container.ban_files_container.name
  }
}
