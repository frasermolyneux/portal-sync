resource "azurerm_resource_group" "legacy_rg" {
  name     = local.legacy_resource_group_name
  location = var.location

  tags = var.tags
}
