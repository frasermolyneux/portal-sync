resource "azurerm_key_vault_secret" "repository_api_subscription_secret" {
  name         = format("%s-apikey", azurerm_api_management_subscription.repository_api_subscription.display_name)
  value        = azurerm_api_management_subscription.repository_api_subscription.primary_key
  key_vault_id = azurerm_key_vault.kv.id
}
