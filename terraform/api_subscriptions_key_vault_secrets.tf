resource "azurerm_key_vault_secret" "repository_api_subscription_secret_primary" {
  name         = format("%s-apikey-primary", azurerm_api_management_subscription.repository_api_subscription.display_name)
  value        = azurerm_api_management_subscription.repository_api_subscription.primary_key
  key_vault_id = azurerm_key_vault.kv.id
}

resource "azurerm_key_vault_secret" "repository_api_subscription_secret_secondary" {
  name         = format("%s-apikey-secondary", azurerm_api_management_subscription.repository_api_subscription.display_name)
  value        = azurerm_api_management_subscription.repository_api_subscription.secondary_key
  key_vault_id = azurerm_key_vault.kv.id
}
