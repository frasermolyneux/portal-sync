resource "azurerm_resource_group" "rg" {
  name     = local.resource_group_name
  location = var.location

  tags = var.tags
}

resource "azurerm_management_lock" "rg_lock" {
  count = var.environment == "prd" ? 1 : 0

  name       = "Terraform (CanNotDelete) - ${random_id.lock.hex}"
  scope      = azurerm_resource_group.rg.id
  lock_level = "CanNotDelete"
  notes      = "CanNotDelete Lock managed by Terraform to prevent manual or accidental deletion of resource group and resources"
}
