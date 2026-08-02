terraform {
  required_version = ">= 1.9.0"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Partial backend configuration on purpose: the bucket and prefix are supplied at
  # init time so the same configuration serves every environment.
  #
  #   terraform init -backend-config="bucket=<your-state-bucket>" \
  #                  -backend-config="prefix=<environment>"
  #
  # The bucket must exist, with versioning enabled, before the first init.
  # See README.md — this configuration deliberately does not create its own state store.
  backend "gcs" {}
}

provider "google" {
  project = var.project_id
  region  = var.region
}
