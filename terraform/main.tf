terraform {
  required_version = ">= 1.1.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.35.0"
    }
  }

  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id = var.subscription_id

  features {
    resource_group {
      # Resource group is only used by workload, App Insights creates artifacts that need to be deleted
      prevent_deletion_if_contains_resources = false
    }
  }
}

provider "azurerm" {
  alias           = "api_management"
  subscription_id = var.api_management_subscription_id

  # This is a workload repository so won't have permissions to register providers
  skip_provider_registration = true

  features {}
}

provider "azurerm" {
  alias           = "web_apps"
  subscription_id = var.web_apps_subscription_id

  # This is a workload repository so won't have permissions to register providers
  skip_provider_registration = true

  features {}
}

provider "azurerm" {
  alias           = "log_analytics"
  subscription_id = var.log_analytics_subscription_id

  # This is a workload repository so won't have permissions to register providers
  skip_provider_registration = true

  features {}
}

data "azurerm_client_config" "current" {}

data "azuread_client_config" "current" {}

resource "random_id" "environment_id" {
  byte_length = 6
}

resource "time_rotating" "thirty_days" {
  rotation_days = 30
}
