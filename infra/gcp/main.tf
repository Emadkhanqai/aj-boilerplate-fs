# ---------------------------------------------------------------------------
# The API and its dependencies on Google Cloud.
#
#   Cloud Run           the API
#   Cloud SQL           SQL Server, private IP only
#   Memorystore         Redis
#   Secret Manager      the connection string the API reads at startup
#   Artifact Registry   the container image the service runs
#   Workload Identity   how CI authenticates, with no stored key
#
# infra/azure/ provisions the same logical shape, so the application runs unchanged
# on either provider. See ADR-0002.
# ---------------------------------------------------------------------------

locals {
  name = "${var.name_prefix}-${var.environment}"

  labels = merge(
    {
      environment = var.environment
      managed-by  = "terraform"
    },
    var.labels
  )

  is_prod = var.environment == "prod"
}

# ---------------------------------------------------------------------------
# Required APIs
# ---------------------------------------------------------------------------

resource "google_project_service" "required" {
  for_each = toset([
    "run.googleapis.com",
    "sqladmin.googleapis.com",
    "redis.googleapis.com",
    "secretmanager.googleapis.com",
    "artifactregistry.googleapis.com",
    "vpcaccess.googleapis.com",
    "servicenetworking.googleapis.com",
    "compute.googleapis.com",
    "iamcredentials.googleapis.com",
    "sts.googleapis.com",
  ])

  service = each.value

  # Leave the APIs enabled when the stack is destroyed; other things in the project
  # may depend on them.
  disable_on_destroy = false
}

# ---------------------------------------------------------------------------
# Networking — private service access for Cloud SQL and Memorystore, plus a
# Serverless VPC Access connector so Cloud Run can reach both.
# ---------------------------------------------------------------------------

data "google_compute_network" "main" {
  name       = var.network_name
  depends_on = [google_project_service.required]
}

resource "google_compute_global_address" "private_service_range" {
  name          = "${local.name}-psa-range"
  purpose       = "VPC_PEERING"
  address_type  = "INTERNAL"
  prefix_length = 16
  network       = data.google_compute_network.main.id
}

resource "google_service_networking_connection" "private_service_access" {
  network                 = data.google_compute_network.main.id
  service                 = "servicenetworking.googleapis.com"
  reserved_peering_ranges = [google_compute_global_address.private_service_range.name]

  depends_on = [google_project_service.required]
}

resource "google_vpc_access_connector" "main" {
  name          = "${local.name}-vpc"
  region        = var.region
  network       = data.google_compute_network.main.name
  ip_cidr_range = var.connector_cidr
  min_instances = 2
  max_instances = 3

  depends_on = [google_project_service.required]
}

# ---------------------------------------------------------------------------
# Artifact Registry
# ---------------------------------------------------------------------------

resource "google_artifact_registry_repository" "containers" {
  location      = var.region
  repository_id = "${local.name}-containers"
  description   = "Container images for the ${var.environment} environment."
  format        = "DOCKER"
  labels        = local.labels

  depends_on = [google_project_service.required]
}

# ---------------------------------------------------------------------------
# Cloud SQL for SQL Server
#
# Private IP only: ipv4_enabled is false, so the instance has no public address and
# is reachable only from the VPC. Do not turn that on for convenience.
# ---------------------------------------------------------------------------

resource "random_password" "sql_root" {
  length  = 32
  special = true
}

resource "random_password" "sql_app" {
  length  = 32
  special = true
}

resource "google_sql_database_instance" "main" {
  name             = "${local.name}-sql"
  database_version = "SQLSERVER_2022_STANDARD"
  region           = var.region
  root_password    = random_password.sql_root.result

  # Guard rail: production instances cannot be destroyed by a terraform apply.
  deletion_protection = local.is_prod

  settings {
    tier              = var.sql_tier
    availability_type = local.is_prod ? "REGIONAL" : "ZONAL"
    disk_size         = var.sql_disk_size_gb
    disk_type         = "PD_SSD"
    disk_autoresize   = true
    user_labels       = local.labels

    backup_configuration {
      enabled                        = true
      start_time                     = "03:00"
      point_in_time_recovery_enabled = false

      backup_retention_settings {
        retained_backups = local.is_prod ? 30 : 7
        retention_unit   = "COUNT"
      }
    }

    ip_configuration {
      ipv4_enabled    = false
      private_network = data.google_compute_network.main.id
      ssl_mode        = "ENCRYPTED_ONLY"
    }

    maintenance_window {
      day          = 7
      hour         = 3
      update_track = "stable"
    }
  }

  depends_on = [google_service_networking_connection.private_service_access]
}

resource "google_sql_database" "app" {
  name     = var.sql_database_name
  instance = google_sql_database_instance.main.name
}

resource "google_sql_user" "app" {
  name     = var.sql_user
  instance = google_sql_database_instance.main.name
  password = random_password.sql_app.result
}

# ---------------------------------------------------------------------------
# Memorystore for Redis
#
# Redis is protocol-identical across providers, so nothing in the application
# changes between here and Azure Cache for Redis — only this file does.
# ---------------------------------------------------------------------------

resource "google_redis_instance" "cache" {
  name               = "${local.name}-redis"
  tier               = local.is_prod ? "STANDARD_HA" : "BASIC"
  memory_size_gb     = var.redis_memory_size_gb
  region             = var.region
  redis_version      = "REDIS_7_0"
  authorized_network = data.google_compute_network.main.id
  connect_mode       = "PRIVATE_SERVICE_ACCESS"
  labels             = local.labels

  depends_on = [google_service_networking_connection.private_service_access]
}

# ---------------------------------------------------------------------------
# Secret Manager
#
# The connection string is assembled here and stored as a secret version. It is
# never an output of this configuration and never appears in a log.
# ---------------------------------------------------------------------------

resource "google_secret_manager_secret" "db_connection" {
  secret_id = "${local.name}-db-connection"
  labels    = local.labels

  replication {
    auto {}
  }

  depends_on = [google_project_service.required]
}

resource "google_secret_manager_secret_version" "db_connection" {
  secret = google_secret_manager_secret.db_connection.id

  secret_data = join(";", [
    "Server=${google_sql_database_instance.main.private_ip_address},1433",
    "Database=${google_sql_database.app.name}",
    "User Id=${google_sql_user.app.name}",
    "Password=${random_password.sql_app.result}",
    "Encrypt=True",
    "TrustServerCertificate=False",
    ""
  ])
}

# ---------------------------------------------------------------------------
# Cloud Run — the API
# ---------------------------------------------------------------------------

resource "google_service_account" "api" {
  account_id   = "${local.name}-api"
  display_name = "API runtime service account (${var.environment})"
}

resource "google_secret_manager_secret_iam_member" "api_reads_db_connection" {
  secret_id = google_secret_manager_secret.db_connection.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.api.email}"
}

resource "google_project_iam_member" "api_cloudsql_client" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = "serviceAccount:${google_service_account.api.email}"
}

resource "google_cloud_run_v2_service" "api" {
  name                = "${local.name}-api"
  location            = var.region
  ingress             = "INGRESS_TRAFFIC_ALL"
  deletion_protection = local.is_prod
  labels              = local.labels

  template {
    service_account = google_service_account.api.email
    labels          = local.labels

    scaling {
      min_instance_count = var.api_min_instances
      max_instance_count = var.api_max_instances
    }

    vpc_access {
      connector = google_vpc_access_connector.main.id
      egress    = "PRIVATE_RANGES_ONLY"
    }

    containers {
      image = var.api_image

      ports {
        container_port = 8080
      }

      resources {
        limits = {
          cpu    = var.api_cpu
          memory = var.api_memory
        }
      }

      # The provider switch the application reads at startup. See ADR-0002.
      env {
        name  = "CLOUD_PROVIDER"
        value = "gcp"
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = local.is_prod ? "Production" : "Staging"
      }

      env {
        name  = "ConnectionStrings__Redis"
        value = "${google_redis_instance.cache.host}:${google_redis_instance.cache.port}"
      }

      # Injected from Secret Manager at start, never baked into the image or this file.
      env {
        name = "ConnectionStrings__Default"

        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.db_connection.secret_id
            version = "latest"
          }
        }
      }

      startup_probe {
        initial_delay_seconds = 10
        period_seconds        = 5
        failure_threshold     = 12

        http_get {
          path = "/health/ready"
        }
      }

      liveness_probe {
        period_seconds = 30

        http_get {
          path = "/health/live"
        }
      }
    }
  }

  depends_on = [
    google_secret_manager_secret_version.db_connection,
    google_secret_manager_secret_iam_member.api_reads_db_connection,
  ]
}

# ---------------------------------------------------------------------------
# Workload identity federation for CI
#
# GitHub Actions exchanges its OIDC token for a short-lived credential. There is no
# service-account key to create, store, rotate, or leak — and none should ever be.
#
# Chicken and egg: CI cannot authenticate until this exists, so it is created by a
# one-off local apply during bootstrap. See README.md.
# ---------------------------------------------------------------------------

resource "google_iam_workload_identity_pool" "github" {
  workload_identity_pool_id = "${local.name}-gh"
  display_name              = "GitHub Actions (${var.environment})"
  description               = "Federated identity for CI in ${var.github_repository}."

  depends_on = [google_project_service.required]
}

resource "google_iam_workload_identity_pool_provider" "github" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "github-oidc"
  display_name                       = "GitHub OIDC"

  attribute_mapping = {
    "google.subject"       = "assertion.sub"
    "attribute.repository" = "assertion.repository"
    "attribute.ref"        = "assertion.ref"
  }

  # Without this condition, any repository on GitHub could assume the CI identity.
  attribute_condition = "assertion.repository == \"${var.github_repository}\""

  oidc {
    issuer_uri = "https://token.actions.githubusercontent.com"
  }
}

resource "google_service_account" "ci" {
  account_id   = "${local.name}-ci"
  display_name = "CI deployment service account (${var.environment})"
}

resource "google_service_account_iam_member" "ci_workload_identity" {
  service_account_id = google_service_account.ci.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.repository/${var.github_repository}"
}

# Deliberately narrow. Widen only with a reason, and never to roles/owner or roles/editor.
resource "google_project_iam_member" "ci_roles" {
  for_each = toset([
    "roles/run.admin",
    "roles/artifactregistry.writer",
    "roles/secretmanager.admin",
    "roles/cloudsql.admin",
    "roles/redis.admin",
    "roles/iam.serviceAccountUser",
  ])

  project = var.project_id
  role    = each.value
  member  = "serviceAccount:${google_service_account.ci.email}"
}
