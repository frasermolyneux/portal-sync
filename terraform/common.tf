resource "azurerm_resource_group" "rg" {
  name     = local.resource_group_name
  location = var.location

  tags = var.tags
}

resource "azurerm_management_lock" "rg_lock" {
  name       = "Terraform (ReadOnly) - ${random_id.lock.hex}"
  scope      = azurerm_resource_group.rg.id
  lock_level = "ReadOnly"
  notes      = "ReadOnly Lock managed by Terraform to prevent manual or accidental modification of resource group and resources"
}
