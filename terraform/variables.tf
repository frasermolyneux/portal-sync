variable "location" {
  default = "uksouth"
}

variable "environment" {
  default = "dev"
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

variable "tags" {
  default = {}
}
