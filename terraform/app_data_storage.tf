resource "azurerm_storage_account" "app_data_storage" {
  name                = local.app_data_storage_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  enable_https_traffic_only = true
  min_tls_version           = "TLS1_2"

  tags = var.tags
}

resource "azurerm_storage_container" "map_images_container" {
  name = "map-images"

  storage_account_name  = azurerm_storage_account.app_data_storage.name
  container_access_type = "blob"
}

resource "azurerm_storage_container" "demos_container" {
  name = "demos"

  storage_account_name  = azurerm_storage_account.app_data_storage.name
  container_access_type = "blob"
}
