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

  // Public network access is required for deployment using public GitHub Actions runners
  public_network_access_enabled = true

  tags = var.tags
}

resource "azurerm_management_lock" "app_data_storage_lock" {
  name       = "Terraform (CanNotDelete) - ${random_id.lock.hex}"
  scope      = azurerm_storage_account.app_data_storage.id
  lock_level = "CanNotDelete"
  notes      = "CanNotDelete Lock managed by Terraform to prevent manual or accidental deletion of resource group and resources"
}

resource "azurerm_storage_container" "ban_files_container" {
  name = "ban-files"

  storage_account_name  = azurerm_storage_account.app_data_storage.name
  container_access_type = "private"
}
