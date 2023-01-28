resource "azurerm_linux_function_app" "app" {
  provider = azurerm.web_apps
  name     = local.function_app_name
  tags     = var.tags

  resource_group_name = data.azurerm_service_plan.plan.resource_group_name
  location            = data.azurerm_service_plan.plan.location
  service_plan_id     = data.azurerm_service_plan.plan.id

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
      dotnet_version              = "7.0"
    }

    ftps_state          = "Disabled"
    always_on           = true
    minimum_tls_version = "1.2"
  }

  app_settings = {
    "READ_ONLY_MODE"                             = var.environment == "prd" ? "true" : "false"
    "WEBSITE_RUN_FROM_PACKAGE"                   = "1"
    "APPINSIGHTS_INSTRUMENTATIONKEY"             = azurerm_application_insights.ai.instrumentation_key
    "APPLICATIONINSIGHTS_CONNECTION_STRING"      = azurerm_application_insights.ai.connection_string
    "ApplicationInsightsAgent_EXTENSION_VERSION" = "~3"
    "apim_base_url"                              = data.azurerm_api_management.platform.gateway_url
    "portal_repository_apim_subscription_key"    = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.kv.name, azurerm_key_vault_secret.repository_api_subscription_secret.name)
    "repository_api_application_audience"        = var.repository_api.application_audience
    "repository_api_path_prefix"                 = var.repository_api.apim_path_prefix
    "map_redirect_base_url"                      = "https://redirect.xtremeidiots.net"
    "map_redirect_api_key"                       = format("@Microsoft.KeyVault(VaultName=%s;SecretName=map-redirect-api-key)", azurerm_key_vault.kv.name)
    "xtremeidiots_forums_base_url"               = "https://www.xtremeidiots.com"
    "xtremeidiots_forums_api_key"                = format("@Microsoft.KeyVault(VaultName=%s;SecretName=xtremeidiots-forums-api-key)", azurerm_key_vault.kv.name)
    "appdata_storage_connectionstring"           = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.kv.name, azurerm_key_vault_secret.app_data_storage_connection_string_secret.name)
    "xtremeidiots_ftp_certificate_thumbprint"    = "65173167144EA988088DA20915ABB83DB27645FA"
  }
}
