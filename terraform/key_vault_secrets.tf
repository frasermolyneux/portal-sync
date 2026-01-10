resource "azurerm_key_vault_secret" "map_redirect_api_key" {
  name         = "map-redirect-api-key"
  value        = "placeholder"
  key_vault_id = azurerm_key_vault.kv.id

  lifecycle {
    ignore_changes = [value]
  }
}

resource "azurerm_key_vault_secret" "xtremeidiots_forums_api_key" {
  name         = "xtremeidiots-forums-api-key"
  value        = "placeholder"
  key_vault_id = azurerm_key_vault.kv.id

  lifecycle {
    ignore_changes = [value]
  }
}
