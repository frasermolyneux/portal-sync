environment = "dev"
location    = "uksouth"

subscription_id = "1b5b28ed-1365-4a48-b285-80f80a6aaa1b"

api_management_subscription_id     = "1b5b28ed-1365-4a48-b285-80f80a6aaa1b"
api_management_resource_group_name = "rg-platform-apim-dev-uksouth"
api_management_name                = "apim-mx-platform-dev-uksouth"

web_apps_subscription_id       = "1b5b28ed-1365-4a48-b285-80f80a6aaa1b"
web_apps_resource_group_name   = "rg-platform-webapps-dev-uksouth"
web_apps_app_service_plan_name = "plan-platform-dev-uksouth"

log_analytics_subscription_id     = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
log_analytics_resource_group_name = "rg-platform-logging-prd-uksouth"
log_analytics_workspace_name      = "log-platform-prd-uksouth"

tags = {
  Environment = "dev",
  Workload    = "portal-sync",
  DeployedBy  = "GitHub-Terraform",
  Git         = "https://github.com/frasermolyneux/portal-sync"
}
