resource "azurerm_storage_account" "function_app_storage" {
  name                = local.function_app_storage_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  https_traffic_only_enabled = true
  min_tls_version            = "TLS1_2"

  tags = var.tags
}
