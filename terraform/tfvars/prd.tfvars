environment = "prd"
location    = "uksouth"

subscription_id = "32444f38-32f4-409f-889c-8e8aa2b5b4d1"

api_management_subscription_id     = "903b6685-c12a-4703-ac54-7ec1ff15ca43"
api_management_resource_group_name = "rg-platform-apim-prd-uksouth"
api_management_name                = "apim-mx-platform-prd-uksouth"

web_apps_subscription_id       = "903b6685-c12a-4703-ac54-7ec1ff15ca43"
web_apps_resource_group_name   = "rg-platform-webapps-prd-uksouth"
web_apps_app_service_plan_name = "plan-platform-prd-uksouth"

log_analytics_subscription_id     = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
log_analytics_resource_group_name = "rg-platform-logging-prd-uksouth"
log_analytics_workspace_name      = "log-platform-prd-uksouth"

tags = {
  Environment = "prd",
  Workload    = "portal-sync",
  DeployedBy  = "GitHub-Terraform",
  Git         = "https://github.com/frasermolyneux/portal-sync"
}
