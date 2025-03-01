resource "azurerm_storage_account" "app_data_storage" {
  name                = local.app_data_storage_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  min_tls_version            = "TLS1_2"
  https_traffic_only_enabled = true

  local_user_enabled        = false
  shared_access_key_enabled = false

  public_network_access_enabled = true

  tags = var.tags
}

//resource "azurerm_management_lock" "app_data_storage_lock" {
//  count = var.environment == "prd" ? 1 : 0
//
//  name       = "Terraform (CanNotDelete) - ${random_id.lock.hex}"
//  scope      = azurerm_storage_account.app_data_storage.id
//  lock_level = "CanNotDelete"
//  notes      = "CanNotDelete Lock managed by Terraform to prevent manual or accidental deletion of resource group and resources"
//}

resource "azurerm_storage_container" "ban_files_container" {
  name = "ban-files"

  storage_account_id    = azurerm_storage_account.app_data_storage.id
  container_access_type = "private"
}
