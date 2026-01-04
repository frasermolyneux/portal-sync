data "azurerm_client_config" "current" {}

data "azuread_client_config" "current" {}

resource "random_id" "legacy_environment_id" {
  byte_length = 6
}

resource "time_rotating" "legacy_thirty_days" {
  rotation_days = 30
}

resource "random_id" "legacy_lock" {
  keepers = {
    id = "${timestamp()}"
  }
  byte_length = 8
}

moved {
  from = random_id.environment_id
  to   = random_id.legacy_environment_id
}

moved {
  from = time_rotating.thirty_days
  to   = time_rotating.legacy_thirty_days
}

moved {
  from = random_id.lock
  to   = random_id.legacy_lock
}
