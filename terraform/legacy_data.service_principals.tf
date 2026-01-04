data "azuread_service_principal" "repository_api" {
  display_name = var.repository_api.application_name
}

data "azuread_service_principal" "servers_integration_api" {
  display_name = var.servers_integration_api.application_name
}
