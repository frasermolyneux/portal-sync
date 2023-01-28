data "azuread_service_principal" "repository_api" {
  display_name = var.repository_api.application_name
}
