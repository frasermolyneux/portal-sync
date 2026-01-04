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
