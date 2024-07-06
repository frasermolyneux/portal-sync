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
    apim_api_name        = string
    apim_api_revision    = string
    apim_path_prefix     = string
  })
  default = {
    application_name     = "portal-repository-dev-01"
    application_audience = "api://portal-repository-dev-01"
    apim_api_name        = "repository-api"
    apim_api_revision    = "1"
    apim_path_prefix     = "repository"
  }
}

variable "tags" {
  default = {}
}
