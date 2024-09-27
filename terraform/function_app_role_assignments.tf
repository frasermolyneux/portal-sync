resource "azurerm_role_assignment" "app-to-storage" {
  scope                = azurerm_storage_account.function_app_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_linux_function_app.app.identity[0].principal_id
}

resource "azurerm_role_assignment" "web_app_kv_role_assignment" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_function_app.app.identity.0.principal_id
}

resource "azuread_app_role_assignment" "repository_api" {
  app_role_id         = data.azuread_service_principal.repository_api.app_roles[index(data.azuread_service_principal.repository_api.app_roles.*.display_name, "ServiceAccount")].id
  principal_object_id = azurerm_linux_function_app.app.identity.0.principal_id
  resource_object_id  = data.azuread_service_principal.repository_api.object_id
}

resource "azuread_app_role_assignment" "servers_integration_api" {
  app_role_id         = data.azuread_service_principal.servers_integration_api.app_roles[index(data.azuread_service_principal.servers_integration_api.app_roles.*.display_name, "ServiceAccount")].id
  principal_object_id = azurerm_linux_function_app.app.identity.0.principal_id
  resource_object_id  = data.azuread_service_principal.servers_integration_api.object_id
}
