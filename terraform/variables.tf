variable "environment" {
  default = "dev"
}

variable "location" {
  default = "uksouth"
}

variable "instance" {
  default = "01"
}

variable "subscription_id" {}

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
