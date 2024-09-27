resource "azurerm_key_vault" "kv" {
  name                = local.key_vault_name
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  tenant_id           = data.azurerm_client_config.current.tenant_id

  tags = var.tags

  soft_delete_retention_days = 90
  purge_protection_enabled   = true
  enable_rbac_authorization  = true

  sku_name = "standard"

  network_acls {
    bypass         = "AzureServices"
    default_action = "Allow"
  }
}

//resource "azurerm_management_lock" "kv_lock" {
//  count = var.environment == "prd" ? 1 : 0
//
//  name       = "Terraform (CanNotDelete) - ${random_id.lock.hex}"
//  scope      = azurerm_key_vault.kv.id
//  lock_level = "CanNotDelete"
//  notes      = "CanNotDelete Lock managed by Terraform to prevent manual or accidental deletion of resource group and resources"
//}
