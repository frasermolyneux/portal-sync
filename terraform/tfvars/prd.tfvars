environment = "prd"
location    = "uksouth"
instance    = "01"

subscription_id = "32444f38-32f4-409f-889c-8e8aa2b5b4d1"

api_management_name = "apim-portal-core-prd-uksouth-01-f4d9512b0e37"

repository_api = {
  application_name     = "portal-repository-prd-01"
  application_audience = "api://portal-repository-prd-01"
  apim_api_name        = "repository-api"
  apim_api_revision    = "1"
  apim_path_prefix     = "repository"
}

tags = {
  Environment = "prd",
  Workload    = "portal",
  DeployedBy  = "GitHub-Terraform",
  Git         = "https://github.com/frasermolyneux/portal-sync"
}
