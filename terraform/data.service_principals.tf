data "azuread_service_principal" "repository_api" {
  display_name = format("portal-repository-%s", var.environment)
}
