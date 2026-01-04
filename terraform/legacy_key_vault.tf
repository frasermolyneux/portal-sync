resource "azurerm_key_vault" "legacy_kv" {
  name                = local.legacy_key_vault_name
  location            = azurerm_resource_group.legacy_rg.location
  resource_group_name = azurerm_resource_group.legacy_rg.name
  tenant_id           = data.azurerm_client_config.current.tenant_id

  tags = var.tags

  soft_delete_retention_days = 90
  purge_protection_enabled   = true
  rbac_authorization_enabled = true

  sku_name = "standard"

  network_acls {
    bypass         = "AzureServices"
    default_action = "Allow"
  }
}

//resource "azurerm_management_lock" "kv_lock" {
//  count = var.environment == "prd" ? 1 : 0
//
//  name       = "Terraform (CanNotDelete) - ${random_id.legacy_lock.hex}"
//  scope      = azurerm_key_vault.legacy_kv.id
//  lock_level = "CanNotDelete"
//  notes      = "CanNotDelete Lock managed by Terraform to prevent manual or accidental deletion of resource group and resources"
//}

moved {
  from = azurerm_key_vault.kv
  to   = azurerm_key_vault.legacy_kv
}
