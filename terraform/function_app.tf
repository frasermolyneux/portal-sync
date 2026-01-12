resource "azurerm_linux_function_app" "function_app" {
  name = local.function_app_name
  tags = var.tags

  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  service_plan_id = data.azurerm_service_plan.sp.id

  storage_account_name          = azurerm_storage_account.function_app_storage.name
  storage_uses_managed_identity = true

  https_only = true

  functions_extension_version = "~4"

  identity {
    type         = "UserAssigned"
    identity_ids = [local.sync_identity.id]
  }

  key_vault_reference_identity_id = local.sync_identity.id

  site_config {
    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "9.0"
    }

    application_insights_connection_string = data.azurerm_application_insights.app_insights.connection_string
    application_insights_key               = data.azurerm_application_insights.app_insights.instrumentation_key

    ftps_state          = "Disabled"
    always_on           = true
    minimum_tls_version = "1.2"

    health_check_path                 = "/api/health"
    health_check_eviction_time_in_min = 5
  }

  app_settings = {
    "AzureAppConfiguration__Endpoint"                = local.app_configuration_endpoint
    "AzureAppConfiguration__ManagedIdentityClientId" = local.sync_identity.client_id
    "AzureAppConfiguration__Environment"             = var.environment

    "AZURE_CLIENT_ID" = local.sync_identity.client_id

    "ApplicationInsightsAgent_EXTENSION_VERSION" = "~3"

    "RepositoryApi__BaseUrl"             = local.repository_api.api_management.endpoint
    "RepositoryApi__ApplicationAudience" = local.repository_api.application.primary_identifier_uri

    "ServersIntegrationApi__BaseUrl"             = local.servers_integration_api.api_management.endpoint
    "ServersIntegrationApi__ApplicationAudience" = local.servers_integration_api.application.primary_identifier_uri

    "map_redirect_base_url" = "https://redirect.xtremeidiots.net"
    "map_redirect_api_key"  = format("@Microsoft.KeyVault(VaultName=%s;SecretName=map-redirect-api-key)", azurerm_key_vault.kv.name)

    "xtremeidiots_forums_base_url" = "https://www.xtremeidiots.com"
    "xtremeidiots_forums_api_key"  = format("@Microsoft.KeyVault(VaultName=%s;SecretName=xtremeidiots-forums-api-key)", azurerm_key_vault.kv.name)

    "appdata_storage_blob_endpoint"           = azurerm_storage_account.app_data_storage.primary_blob_endpoint
    "xtremeidiots_ftp_certificate_thumbprint" = "65173167144EA988088DA20915ABB83DB27645FA"

    // https://learn.microsoft.com/en-us/azure/azure-monitor/profiler/profiler-azure-functions#app-settings-for-enabling-profiler
    "APPINSIGHTS_PROFILERFEATURE_VERSION"  = "1.0.0"
    "DiagnosticServices_EXTENSION_VERSION" = "~3"
  }

  lifecycle {
    ignore_changes = [
      app_settings["WEBSITE_RUN_FROM_PACKAGE"] # Ignore changes to this property as it will be updated by the deployment pipeline
    ]
  }
}
