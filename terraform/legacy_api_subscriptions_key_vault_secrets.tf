resource "azurerm_key_vault_secret" "legacy_repository_api_subscription_secret_primary" {
  name         = format("%s-api-key-primary", azurerm_api_management_subscription.legacy_repository_api_subscription.display_name)
  value        = azurerm_api_management_subscription.legacy_repository_api_subscription.primary_key
  key_vault_id = azurerm_key_vault.legacy_kv.id
}

resource "azurerm_key_vault_secret" "legacy_repository_api_subscription_secret_secondary" {
  name         = format("%s-api-key-secondary", azurerm_api_management_subscription.legacy_repository_api_subscription.display_name)
  value        = azurerm_api_management_subscription.legacy_repository_api_subscription.secondary_key
  key_vault_id = azurerm_key_vault.legacy_kv.id
}

resource "azurerm_key_vault_secret" "legacy_servers_integration_api_subscription_secret" {
  name         = format("%s-api-key", azurerm_api_management_subscription.legacy_servers_integration_api_subscription.display_name)
  value        = azurerm_api_management_subscription.legacy_servers_integration_api_subscription.primary_key
  key_vault_id = azurerm_key_vault.legacy_kv.id
}
