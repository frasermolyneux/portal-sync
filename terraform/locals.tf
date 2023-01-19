locals {
  resource_group_name       = "rg-portal-sync-${var.environment}-${var.location}"
  key_vault_name            = "kv-${random_id.environment_id.hex}-${var.location}"
  app_insights_name         = "ai-ptl-sync-${random_id.environment_id.hex}-${var.environment}-${var.location}"
  function_app_name         = "fa-ptl-sync-${random_id.environment_id.hex}-${var.environment}-${var.location}"
  function_app_storage_name = "saptlsyncfn${random_id.environment_id.hex}"
  app_data_storage_name     = "saptlsyncad${random_id.environment_id.hex}"
}
