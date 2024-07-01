data "azurerm_client_config" "current" {}

data "azuread_client_config" "current" {}

resource "random_id" "environment_id" {
  byte_length = 6
}

resource "time_rotating" "thirty_days" {
  rotation_days = 30
}

resource "random_id" "lock" {
  keepers = {
    id = "${timestamp()}"
  }
  byte_length = 8
}
