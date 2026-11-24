# ---------------------------------------------------------------------------
# The Auth service (AjBoilerplate.Auth) and its migration job.
#
#   Cloud Run service   auth          signs users in and answers the gateway's auth_request
#   Cloud Run job       auth-migrate  applies the Auth service's EF Core migrations
#   Cloud SQL database  on the same instance as the API, its own login
#   Secret Manager      one secret per credential, referenced by name, never inlined
#
# Only the web container (the nginx gateway, web.tf) calls the service. That is enforced by IAM,
# not by network placement: see "Who may call the API and the Auth service" in web.tf.
#
# Keycloak and the identity provider are EXTERNAL. This file never deploys either; it is told where
# they are by variables, and reads the two client secrets from Secret Manager secrets the operator
# creates first (see README.md).
# ---------------------------------------------------------------------------

data "google_project" "this" {
  project_id = var.project_id
}

locals {
  # The browser-facing origin, which the Auth service builds its redirect URI from
  # (`<origin>/auth/callback`) and checks logout's Origin against. It cannot be read back from the
  # web service: the web service reads the Auth service's address, so that would be a cycle. The
  # deterministic run.app form is used instead. Set `public_origin` when a custom domain or a load
  # balancer is in front.
  public_origin = coalesce(var.public_origin, "https://${local.name}-web-${data.google_project.this.number}.${var.region}.run.app")

  keycloak_issuer   = coalesce(var.keycloak_issuer, "${trimsuffix(var.keycloak_base_url, "/")}/realms/${var.keycloak_realm}")
  keycloak_jwks_url = coalesce(var.keycloak_jwks_url, "${local.keycloak_issuer}/protocol/openid-connect/certs")

  entra_client_secret_name    = coalesce(var.entra_client_secret_name, "${local.name}-entra-client-secret")
  keycloak_client_secret_name = coalesce(var.keycloak_client_secret_name, "${local.name}-keycloak-client-secret")
  auth_secret_ids = merge(
    {
      auth_db_connection = google_secret_manager_secret.auth_db_connection.id
      session_key        = google_secret_manager_secret.auth_session_key.id
      entra_client       = data.google_secret_manager_secret.entra_client.id
      keycloak_client    = data.google_secret_manager_secret.keycloak_client.id
    },
    var.auth_admin_api_key_enabled ? { admin_api_key = google_secret_manager_secret.auth_admin_api_key[0].id } : {}
  )
}

# ---------------------------------------------------------------------------
# Database — the Auth service's own database and login on the shared Cloud SQL instance.
# Sessions are its system of record, so it gets a database of its own rather than a schema in the
# API's.
# ---------------------------------------------------------------------------

resource "random_password" "sql_auth" {
  length  = 32
  special = true
}

resource "google_sql_database" "auth" {
  name     = var.auth_sql_database_name
  instance = google_sql_database_instance.main.name
}

resource "google_sql_user" "auth" {
  name     = var.auth_sql_user
  instance = google_sql_database_instance.main.name
  password = random_password.sql_auth.result
}

# ---------------------------------------------------------------------------
# Secrets
#
# Generated here, stored once, never an output:
#   auth-db-connection   assembled like the API's; the login's password is generated above
#   auth-session-key     base64 of 32 random bytes; encrypts the JWT and refresh token at rest.
#                        Rotating it signs every user out (their stored sessions become unreadable).
#   auth-admin-api-key   only when auth_admin_api_key_enabled; empty disables /internal/admin/*
#
# Created by the operator BEFORE the first apply, read here by name (a missing one fails the plan
# with a message naming it, which is the point): the entra and keycloak client secrets. They come
# from a registration this configuration does not own, so no value of theirs can be generated here.
# ---------------------------------------------------------------------------

resource "google_secret_manager_secret" "auth_db_connection" {
  secret_id = "${local.name}-auth-db-connection"
  labels    = local.labels

  replication {
    auto {}
  }

  depends_on = [google_project_service.required]
}

resource "google_secret_manager_secret_version" "auth_db_connection" {
  secret = google_secret_manager_secret.auth_db_connection.id

  secret_data = join(";", [
    "Server=${google_sql_database_instance.main.private_ip_address},1433",
    "Database=${google_sql_database.auth.name}",
    "User Id=${google_sql_user.auth.name}",
    "Password=${random_password.sql_auth.result}",
    "Encrypt=True",
    "TrustServerCertificate=False",
    ""
  ])
}

resource "random_id" "auth_session_key" {
  byte_length = 32
}

resource "google_secret_manager_secret" "auth_session_key" {
  secret_id = "${local.name}-auth-session-key"
  labels    = local.labels

  replication {
    auto {}
  }

  depends_on = [google_project_service.required]
}

resource "google_secret_manager_secret_version" "auth_session_key" {
  secret      = google_secret_manager_secret.auth_session_key.id
  secret_data = random_id.auth_session_key.b64_std
}

resource "random_password" "auth_admin_api_key" {
  count   = var.auth_admin_api_key_enabled ? 1 : 0
  length  = 48
  special = false
}

resource "google_secret_manager_secret" "auth_admin_api_key" {
  count     = var.auth_admin_api_key_enabled ? 1 : 0
  secret_id = "${local.name}-auth-admin-api-key"
  labels    = local.labels

  replication {
    auto {}
  }

  depends_on = [google_project_service.required]
}

resource "google_secret_manager_secret_version" "auth_admin_api_key" {
  count       = var.auth_admin_api_key_enabled ? 1 : 0
  secret      = google_secret_manager_secret.auth_admin_api_key[0].id
  secret_data = random_password.auth_admin_api_key[0].result
}

data "google_secret_manager_secret" "entra_client" {
  secret_id  = local.entra_client_secret_name
  depends_on = [google_project_service.required]
}

data "google_secret_manager_secret" "keycloak_client" {
  secret_id  = local.keycloak_client_secret_name
  depends_on = [google_project_service.required]
}

# ---------------------------------------------------------------------------
# Identities. One per workload, each able to read only the secrets it uses.
# ---------------------------------------------------------------------------

resource "google_service_account" "auth" {
  account_id   = "${local.name}-auth"
  display_name = "Auth service runtime service account (${var.environment})"
}

resource "google_service_account" "auth_migrate" {
  account_id   = "${local.name}-auth-mig"
  display_name = "Auth migration job service account (${var.environment})"
}

resource "google_secret_manager_secret_iam_member" "auth_reads" {
  for_each = local.auth_secret_ids

  secret_id = each.value
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.auth.email}"
}

resource "google_secret_manager_secret_iam_member" "auth_migrate_reads_db_connection" {
  secret_id = google_secret_manager_secret.auth_db_connection.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.auth_migrate.email}"
}

resource "google_project_iam_member" "auth_cloudsql_client" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = "serviceAccount:${google_service_account.auth.email}"
}

# ---------------------------------------------------------------------------
# Cloud Run job — apply the Auth service's EF Core migrations.
#
# A discrete release step, never an application start-up side effect and never EnsureCreated: the
# deploy workflow executes it, and waits for it, before the Auth service is rolled out. It runs the
# Auth Dockerfile's `build` stage, which carries dotnet-ef (the runtime stage deliberately does
# not), exactly as docker-compose's `auth-migrate` does. The design-time factory reads
# AUTH_DB_CONNECTION; the host is never started.
#
# The API's migrations are applied differently (a bundle built by Backend CI, run through the Cloud
# SQL Auth Proxy by deploy-environment.yml). Backend CI builds no bundle for the Auth service, so
# its migrations ship in this image instead.
# ---------------------------------------------------------------------------

resource "google_cloud_run_v2_job" "auth_migrate" {
  name                = "${local.name}-auth-migrate"
  location            = var.region
  deletion_protection = local.is_prod
  labels              = local.labels

  template {
    labels = local.labels

    # Re-running is safe (EF applies only what is outstanding), but a failed migration must stop the
    # release, not retry on its own.
    task_count  = 1
    parallelism = 1

    template {
      service_account = google_service_account.auth_migrate.email
      max_retries     = 0
      timeout         = "900s"

      vpc_access {
        connector = google_vpc_access_connector.main.id
        egress    = "PRIVATE_RANGES_ONLY"
      }

      containers {
        image       = var.auth_migrate_image
        working_dir = "/src"
        command     = ["dotnet-ef"]
        args = [
          "database", "update",
          "--project", "src/AjBoilerplate.Auth/AjBoilerplate.Auth.csproj",
          "--startup-project", "src/AjBoilerplate.Auth/AjBoilerplate.Auth.csproj",
        ]

        # The SDK compiles the project before applying, which needs more than a service does.
        resources {
          limits = {
            cpu    = "2"
            memory = "2Gi"
          }
        }

        env {
          name = "AUTH_DB_CONNECTION"

          value_source {
            secret_key_ref {
              secret  = google_secret_manager_secret.auth_db_connection.secret_id
              version = "latest"
            }
          }
        }
      }
    }
  }

  depends_on = [
    google_secret_manager_secret_version.auth_db_connection,
    google_secret_manager_secret_iam_member.auth_migrate_reads_db_connection,
  ]
}

# ---------------------------------------------------------------------------
# Cloud Run service — the Auth service.
#
# Environment keys are the .NET configuration keys with `:` written `__` (checked against
# AuthOptions.cs and appsettings.json). The service has no secret-store loader, so every secret
# arrives as an environment variable by Secret Manager reference.
#
# Egress is PRIVATE_RANGES_ONLY, as for the API: Redis and SQL are reached through the connector,
# while the identity provider and Keycloak are reached directly. ALL_TRAFFIC would need a Cloud NAT
# that this configuration does not create.
# ---------------------------------------------------------------------------

resource "google_cloud_run_v2_service" "auth" {
  name                = "${local.name}-auth"
  location            = var.region
  ingress             = "INGRESS_TRAFFIC_ALL"
  deletion_protection = local.is_prod
  labels              = local.labels

  template {
    service_account = google_service_account.auth.email
    labels          = local.labels

    scaling {
      min_instance_count = var.auth_min_instances
      max_instance_count = var.auth_max_instances
    }

    vpc_access {
      connector = google_vpc_access_connector.main.id
      egress    = "PRIVATE_RANGES_ONLY"
    }

    containers {
      image = var.auth_image

      ports {
        container_port = 8080
      }

      resources {
        limits = {
          cpu    = var.auth_cpu
          memory = var.auth_memory
        }
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = local.is_prod ? "Production" : "Staging"
      }

      env {
        name  = "ConnectionStrings__Redis"
        value = "${google_redis_instance.cache.host}:${google_redis_instance.cache.port}"
      }

      env {
        name  = "Auth__PublicOrigin"
        value = local.public_origin
      }

      env {
        name  = "Auth__Entra__Authority"
        value = var.entra_authority
      }

      env {
        name  = "Auth__Entra__ClientId"
        value = var.entra_client_id
      }

      env {
        name  = "Auth__Entra__Scopes"
        value = var.entra_scopes
      }

      env {
        name  = "Auth__Entra__RequestOwnClientIdScope"
        value = tostring(var.entra_request_own_client_id_scope)
      }

      env {
        name  = "Auth__Keycloak__BaseUrl"
        value = var.keycloak_base_url
      }

      env {
        name  = "Auth__Keycloak__Realm"
        value = var.keycloak_realm
      }

      env {
        name  = "Auth__Keycloak__SubjectIssuer"
        value = var.keycloak_subject_issuer
      }

      env {
        name  = "Auth__Portal__ClientId"
        value = var.keycloak_portal_client_id
      }

      env {
        name = "ConnectionStrings__AuthDb"

        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.auth_db_connection.secret_id
            version = "latest"
          }
        }
      }

      env {
        name = "Auth__Session__EncryptionKey"

        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.auth_session_key.secret_id
            version = "latest"
          }
        }
      }

      env {
        name = "Auth__Entra__ClientSecret"

        value_source {
          secret_key_ref {
            secret  = data.google_secret_manager_secret.entra_client.secret_id
            version = "latest"
          }
        }
      }

      env {
        name = "Auth__Portal__ClientSecret"

        value_source {
          secret_key_ref {
            secret  = data.google_secret_manager_secret.keycloak_client.secret_id
            version = "latest"
          }
        }
      }

      dynamic "env" {
        for_each = var.auth_admin_api_key_enabled ? [1] : []

        content {
          name = "Auth__Admin__ApiKey"

          value_source {
            secret_key_ref {
              secret  = google_secret_manager_secret.auth_admin_api_key[0].secret_id
              version = "latest"
            }
          }
        }
      }

      # Readiness pings Redis and SQL; liveness touches nothing, so a dependency outage cannot
      # restart-loop the service.
      startup_probe {
        initial_delay_seconds = 10
        period_seconds        = 5
        failure_threshold     = 12

        http_get {
          path = "/readyz"
        }
      }

      liveness_probe {
        period_seconds = 30

        http_get {
          path = "/healthz"
        }
      }
    }
  }

  depends_on = [
    google_secret_manager_secret_version.auth_db_connection,
    google_secret_manager_secret_version.auth_session_key,
    google_secret_manager_secret_iam_member.auth_reads,
    google_cloud_run_v2_job.auth_migrate,
  ]
}
