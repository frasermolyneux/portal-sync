provider "azurerm" {
  alias           = "azure_monitor"
  subscription_id = var.environment == "prd" ? "7760848c-794d-4a19-8cb2-52f71a21ac2b" : "d68448b0-9947-46d7-8771-baa331a3063a"
  features {}
  storage_use_azuread = true
}

locals {
  azure_monitor_resource_group = "rg-platform-monitoring-${var.environment}-uksouth"
}

data "azurerm_monitor_action_group" "critical" {
  provider = azurerm.azure_monitor

  name                = "p0-critical-alerts-${var.environment}"
  resource_group_name = local.azure_monitor_resource_group
}

data "azurerm_monitor_action_group" "high" {
  provider = azurerm.azure_monitor

  name                = "p1-high-alerts-${var.environment}"
  resource_group_name = local.azure_monitor_resource_group
}

data "azurerm_monitor_action_group" "moderate" {
  provider = azurerm.azure_monitor

  name                = "p2-moderate-alerts-${var.environment}"
  resource_group_name = local.azure_monitor_resource_group
}

data "azurerm_monitor_action_group" "low" {
  provider = azurerm.azure_monitor

  name                = "p3-low-alerts-${var.environment}"
  resource_group_name = local.azure_monitor_resource_group
}

data "azurerm_monitor_action_group" "informational" {
  provider = azurerm.azure_monitor

  name                = "p4-informational-alerts-${var.environment}"
  resource_group_name = local.azure_monitor_resource_group
}
