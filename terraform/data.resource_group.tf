data "azurerm_resource_group" "core" {
  name = "rg-portal-core-${var.environment}-${var.location}-${var.instance}"
}
