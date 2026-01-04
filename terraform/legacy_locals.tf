locals {
  # Remote State References
  workload_resource_groups = {
    for location in [var.location] :
    location => data.terraform_remote_state.platform_workloads.outputs.workload_resource_groups[var.workload_name][var.environment].resource_groups[lower(location)]
  }

  workload_backend = try(
    data.terraform_remote_state.platform_workloads.outputs.workload_terraform_backends[var.workload_name][var.environment],
    null
  )

  workload_administrative_unit = try(
    data.terraform_remote_state.platform_workloads.outputs.workload_administrative_units[var.workload_name][var.environment],
    null
  )

  workload_resource_group = local.workload_resource_groups[var.location]

  # Local Resource Naming
  legacy_resource_group_name       = "rg-portal-sync-${var.environment}-${var.location}-${var.instance}"
  legacy_key_vault_name            = substr(format("kv-%s-%s", random_id.legacy_environment_id.hex, var.location), 0, 24)
  legacy_app_insights_name         = "ai-portal-sync-${var.environment}-${var.location}-${var.instance}"
  legacy_function_app_name         = "fn-portal-sync-${var.environment}-${var.location}-${var.instance}-${random_id.legacy_environment_id.hex}"
  legacy_function_app_storage_name = "safn${random_id.legacy_environment_id.hex}"
  legacy_app_data_storage_name     = "saad${random_id.legacy_environment_id.hex}"
}
