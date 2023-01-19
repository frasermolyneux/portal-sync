resource "azurerm_key_vault_secret" "app_data_storage_connection_string_secret" {
  name         = "${azurerm_storage_account.app_data_storage.name}-connectionstring"
  value        = format("DefaultEndpointsProtocol=https;AccountName=%s;EndpointSuffix=core.windows.net;AccountKey=%s", azurerm_storage_account.app_data_storage.name, azurerm_storage_account.app_data_storage.primary_access_key)
  key_vault_id = azurerm_key_vault.kv.id

  depends_on = [
    azurerm_role_assignment.deploy_principal_kv_role_assignment
  ]
}
