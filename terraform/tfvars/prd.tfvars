environment = "prd"
location    = "uksouth"
instance    = "01"

subscription_id = "32444f38-32f4-409f-889c-8e8aa2b5b4d1"

platform_workloads_state = {
  resource_group_name  = "rg-tf-platform-workloads-prd-uksouth-01"
  storage_account_name = "sadz9ita659lj9xb3"
  container_name       = "tfstate"
  key                  = "terraform.tfstate"
  subscription_id      = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
  tenant_id            = "e56a6947-bb9a-4a6e-846a-1f118d1c3a14"
}

platform_monitoring_state = {
  resource_group_name  = "rg-tf-platform-monitoring-prd-uksouth-01"
  storage_account_name = "sa74f04c5f984e"
  container_name       = "tfstate"
  key                  = "terraform.tfstate"
  subscription_id      = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
  tenant_id            = "e56a6947-bb9a-4a6e-846a-1f118d1c3a14"
}

portal_environments_state = {
  resource_group_name  = "rg-tf-portal-environments-prd-uksouth-01"
  storage_account_name = "sad74a6da165e7"
  container_name       = "tfstate"
  key                  = "terraform.tfstate"
  subscription_id      = "7760848c-794d-4a19-8cb2-52f71a21ac2b"
  tenant_id            = "e56a6947-bb9a-4a6e-846a-1f118d1c3a14"
}

api_management_name = "apim-portal-core-prd-uksouth-01-f4d9512b0e37"

repository_api = {
  application_name     = "portal-repository-prd-01"
  application_audience = "api://e56a6947-bb9a-4a6e-846a-1f118d1c3a14/portal-repository-prd-01"
  apim_product_id      = "repository-api"
}

servers_integration_api = {
  application_name     = "portal-servers-integration-prd-01"
  application_audience = "api://portal-servers-integration-prd-01"
  apim_product_id      = "servers-integration-api"
}

tags = {
  Environment = "prd",
  Workload    = "portal-sync",
  DeployedBy  = "GitHub-Terraform",
  Git         = "https://github.com/frasermolyneux/portal-sync"
}
