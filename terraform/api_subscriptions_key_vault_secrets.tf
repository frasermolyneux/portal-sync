resource "azurerm_key_vault_secret" "repository_api_subscription_secret" {
  name         = format("%s-apikey", azurerm_api_management_subscription.repository_api_subscription.display_name)
  value        = azurerm_api_management_subscription.repository_api_subscription.primary_key
  key_vault_id = azurerm_key_vault.kv.id

  depends_on = [
    azurerm_role_assignment.deploy_principal_kv_role_assignment
  ]
}
