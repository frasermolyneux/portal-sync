resource "azurerm_storage_account" "legacy_function_app_storage" {
  name                = local.legacy_function_app_storage_name
  resource_group_name = azurerm_resource_group.legacy_rg.name
  location            = azurerm_resource_group.legacy_rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  https_traffic_only_enabled = true
  min_tls_version            = "TLS1_2"

  local_user_enabled        = false
  shared_access_key_enabled = false

  tags = var.tags
}

moved {
  from = azurerm_storage_account.function_app_storage
  to   = azurerm_storage_account.legacy_function_app_storage
}
