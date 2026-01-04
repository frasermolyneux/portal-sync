output "function_app_name" {
  value = azurerm_linux_function_app.legacy_app.name
}

output "resource_group_name" {
  value = azurerm_resource_group.legacy_rg.name
}
