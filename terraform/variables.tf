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

variable "api_management_subscription_id" {}
variable "api_management_resource_group_name" {}
variable "api_management_name" {}

variable "web_apps_subscription_id" {}
variable "web_apps_resource_group_name" {}
variable "web_apps_app_service_plan_name" {}

variable "log_analytics_subscription_id" {}
variable "log_analytics_resource_group_name" {}
variable "log_analytics_workspace_name" {}

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
