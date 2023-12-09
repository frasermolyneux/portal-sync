resource "azurerm_storage_account" "app_data_storage" {
  name                = local.app_data_storage_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  min_tls_version           = "TLS1_2"
  enable_https_traffic_only = true

  public_network_access_enabled = false

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }

  tags = var.tags
}

resource "azurerm_storage_container" "ban_files_container" {
  name = "ban-files"

  storage_account_name  = azurerm_storage_account.app_data_storage.name
  container_access_type = "blob"
}
