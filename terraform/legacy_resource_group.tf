resource "azurerm_resource_group" "legacy_rg" {
  name     = local.legacy_resource_group_name
  location = var.location

  tags = var.tags
}

moved {
  from = azurerm_resource_group.rg
  to   = azurerm_resource_group.legacy_rg
}

//resource "azurerm_management_lock" "rg_lock" {
//  count = var.environment == "prd" ? 1 : 0
//
//  name       = "Terraform (CanNotDelete) - ${random_id.legacy_lock.hex}"
//  scope      = azurerm_resource_group.legacy_rg.id
//  lock_level = "CanNotDelete"
//  notes      = "CanNotDelete Lock managed by Terraform to prevent manual or accidental deletion of resource group and resources"
//}
