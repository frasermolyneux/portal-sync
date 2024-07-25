resource "azurerm_linux_function_app" "app" {
  name = local.function_app_name
  tags = var.tags

  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  service_plan_id = data.azurerm_service_plan.core.id

  storage_account_name       = azurerm_storage_account.function_app_storage.name
  storage_account_access_key = azurerm_storage_account.function_app_storage.primary_access_key

  https_only = true

  functions_extension_version = "~4"

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "8.0"
    }

    application_insights_connection_string = data.azurerm_application_insights.core.connection_string
    application_insights_key               = data.azurerm_application_insights.core.instrumentation_key

    ftps_state          = "Disabled"
    always_on           = true
    minimum_tls_version = "1.2"

    health_check_path = "/api/health"
  }

  app_settings = {
    "READ_ONLY_MODE"                                    = var.environment == "prd" ? "true" : "false"
    "WEBSITE_RUN_FROM_PACKAGE"                          = "1"
    "ApplicationInsightsAgent_EXTENSION_VERSION"        = "~3"
    "apim_base_url"                                     = data.azurerm_api_management.core.gateway_url
    "portal_repository_apim_subscription_key_primary"   = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.kv.name, azurerm_key_vault_secret.repository_api_subscription_secret_primary.name)
    "portal_repository_apim_subscription_key_secondary" = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.kv.name, azurerm_key_vault_secret.repository_api_subscription_secret_secondary.name)
    "repository_api_application_audience"               = var.repository_api.application_audience
    "repository_api_path_prefix"                        = var.repository_api.apim_path_prefix
    "map_redirect_base_url"                             = "https://redirect.xtremeidiots.net"
    "map_redirect_api_key"                              = format("@Microsoft.KeyVault(VaultName=%s;SecretName=map-redirect-api-key)", azurerm_key_vault.kv.name)
    "xtremeidiots_forums_base_url"                      = "https://www.xtremeidiots.com"
    "xtremeidiots_forums_api_key"                       = format("@Microsoft.KeyVault(VaultName=%s;SecretName=xtremeidiots-forums-api-key)", azurerm_key_vault.kv.name)
    "appdata_storage_connectionstring"                  = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.kv.name, azurerm_key_vault_secret.app_data_storage_connection_string_secret.name)
    "xtremeidiots_ftp_certificate_thumbprint"           = "65173167144EA988088DA20915ABB83DB27645FA"

    // https://learn.microsoft.com/en-us/azure/azure-monitor/profiler/profiler-azure-functions#app-settings-for-enabling-profiler
    "APPINSIGHTS_PROFILERFEATURE_VERSION"  = "1.0.0"
    "DiagnosticServices_EXTENSION_VERSION" = "~3"
  }
}

#resource "azurerm_application_insights_standard_web_test" "app" {
#  count = var.environment == "prd" ? 1 : 0
#  name  = "${azurerm_linux_function_app.app.name}-availability-test"
#  tags  = var.tags
#
#  resource_group_name = data.azurerm_application_insights.core.resource_group_name
#  location            = data.azurerm_application_insights.core.location
#
#  application_insights_id = data.azurerm_application_insights.core.id
#
#  enabled   = true
#  frequency = 900
#
#  geo_locations = [
#    "emea-ru-msa-edge", // UK South
#    "us-va-ash-azr"     // East US
#  ]
#
#  request {
#    url                              = "https://${azurerm_linux_function_app.app.default_hostname}/api/health"
#    http_verb                        = "GET"
#    parse_dependent_requests_enabled = true
#    follow_redirects_enabled         = true
#  }
#
#  validation_rules {
#    expected_status_code        = 200
#    ssl_check_enabled           = true
#    ssl_cert_remaining_lifetime = 14
#  }
#}
