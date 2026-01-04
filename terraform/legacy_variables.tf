variable "environment" {
  default = "dev"
}

variable "workload_name" {
  description = "Name of the workload as defined in platform-workloads state"
  type        = string
  default     = "portal-sync"
}

variable "location" {
  default = "uksouth"
}

variable "instance" {
  default = "01"
}

variable "subscription_id" {}

variable "platform_workloads_state" {
  description = "Backend config for platform-workloads remote state (used to read workload resource groups/backends)"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "platform_monitoring_state" {
  description = "Backend config for platform-monitoring remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "portal_environments_state" {
  description = "Backend config for portal-environments remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "api_management_name" {}

variable "repository_api" {
  type = object({
    application_name     = string
    application_audience = string
    apim_product_id      = string
  })
  default = {
    application_name     = "portal-repository-dev-01"
    application_audience = "api://e56a6947-bb9a-4a6e-846a-1f118d1c3a14/portal-repository-dev-01"
    apim_product_id      = ""
  }
}

variable "servers_integration_api" {
  type = object({
    application_name     = string
    application_audience = string
    apim_product_id      = string
  })
  default = {
    application_name     = "portal-servers-integration-dev-01"
    application_audience = "api://portal-servers-integration-dev-01"
    apim_product_id      = ""
  }
}

variable "tags" {
  default = {}
}
