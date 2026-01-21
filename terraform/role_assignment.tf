resource "azurerm_role_assignment" "app-to-storage" {
  scope                = azurerm_storage_account.function_app_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = local.sync_identity.principal_id
}

resource "azurerm_role_assignment" "app-to-app-data-storage" {
  scope                = azurerm_storage_account.app_data_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = local.sync_identity.principal_id
}

resource "azurerm_role_assignment" "app-to-key-vault" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = local.sync_identity.principal_id
}
