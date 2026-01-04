resource "azurerm_linux_function_app" "legacy_app" {
  name = local.legacy_function_app_name
  tags = var.tags

  resource_group_name = azurerm_resource_group.legacy_rg.name
  location            = azurerm_resource_group.legacy_rg.location

  service_plan_id = data.azurerm_service_plan.core.id

  storage_account_name          = azurerm_storage_account.legacy_function_app_storage.name
  storage_uses_managed_identity = true

  https_only = true

  functions_extension_version = "~4"

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "9.0"
    }

    application_insights_connection_string = data.azurerm_application_insights.core.connection_string
    application_insights_key               = data.azurerm_application_insights.core.instrumentation_key

    ftps_state          = "Disabled"
    always_on           = true
    minimum_tls_version = "1.2"

    health_check_path                 = "/api/health"
    health_check_eviction_time_in_min = 5
  }

  app_settings = {
    "WEBSITE_RUN_FROM_PACKAGE"                   = "1"
    "ApplicationInsightsAgent_EXTENSION_VERSION" = "~3"

    "RepositoryApi__BaseUrl"             = format("%s/repository", data.azurerm_api_management.core.gateway_url)
    "RepositoryApi__ApiKey"              = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.legacy_kv.name, azurerm_key_vault_secret.legacy_repository_api_subscription_secret_primary.name)
    "RepositoryApi__ApplicationAudience" = var.repository_api.application_audience

    "ServersIntegrationApi__BaseUrl"             = "${data.azurerm_api_management.core.gateway_url}/servers-integration"
    "ServersIntegrationApi__ApiKey"              = format("@Microsoft.KeyVault(VaultName=%s;SecretName=%s)", azurerm_key_vault.legacy_kv.name, azurerm_key_vault_secret.legacy_servers_integration_api_subscription_secret.name)
    "ServersIntegrationApi__ApplicationAudience" = var.servers_integration_api.application_audience

    "map_redirect_base_url" = "https://redirect.xtremeidiots.net"
    "map_redirect_api_key"  = format("@Microsoft.KeyVault(VaultName=%s;SecretName=map-redirect-api-key)", azurerm_key_vault.legacy_kv.name)

    "xtremeidiots_forums_base_url" = "https://www.xtremeidiots.com"
    "xtremeidiots_forums_api_key"  = format("@Microsoft.KeyVault(VaultName=%s;SecretName=xtremeidiots-forums-api-key)", azurerm_key_vault.legacy_kv.name)

    "appdata_storage_blob_endpoint"           = azurerm_storage_account.legacy_app_data_storage.primary_blob_endpoint
    "xtremeidiots_ftp_certificate_thumbprint" = "65173167144EA988088DA20915ABB83DB27645FA"

    // https://learn.microsoft.com/en-us/azure/azure-monitor/profiler/profiler-azure-functions#app-settings-for-enabling-profiler
    "APPINSIGHTS_PROFILERFEATURE_VERSION"  = "1.0.0"
    "DiagnosticServices_EXTENSION_VERSION" = "~3"
  }
}
