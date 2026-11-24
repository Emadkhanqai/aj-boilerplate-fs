# No variable in this file has a value that identifies a real project, organisation,
# or environment. Supply them at plan time (CI sets TF_VAR_* from repository variables)
# or in a local, gitignored .tfvars file. See terraform.tfvars.example.

variable "project_id" {
  description = "Google Cloud project ID that owns every resource here."
  type        = string

  validation {
    condition     = length(var.project_id) > 0
    error_message = "project_id must be set."
  }
}

variable "region" {
  description = "Region for all regional resources."
  type        = string
  default     = "europe-west1"
}

variable "environment" {
  description = "Deployment environment. Drives sizing, high availability, and deletion protection."
  type        = string

  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "environment must be one of: dev, staging, prod."
  }
}

variable "name_prefix" {
  description = <<-EOT
    Short prefix for every resource name. Keep it to 12 characters or fewer:
    the Serverless VPC Access connector name is capped at 25 characters and the
    workload identity pool ID at 32.
  EOT
  type        = string
  default     = "app"

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,11}$", var.name_prefix))
    error_message = "name_prefix must be 2-12 lowercase alphanumeric or hyphen characters, starting with a letter."
  }
}

variable "github_repository" {
  description = <<-EOT
    The repository allowed to impersonate the CI service account through workload
    identity federation, as "owner/repo". This is the only repository that can obtain
    credentials for this project — keep it exact.
  EOT
  type        = string

  validation {
    condition     = can(regex("^[^/]+/[^/]+$", var.github_repository))
    error_message = "github_repository must be in the form owner/repo."
  }
}

variable "api_image" {
  description = "Fully qualified container image for the API, including its tag or digest."
  type        = string
}

variable "auth_image" {
  description = "Fully qualified container image for the Auth service (the `runtime` stage of src/backend/src/AjBoilerplate.Auth/Dockerfile), including its tag or digest."
  type        = string
}

variable "auth_migrate_image" {
  description = <<-EOT
    Fully qualified container image for the Auth migration job: the SAME Dockerfile built with
    `--target build`, which is the stage that carries dotnet-ef. The runtime image has no dotnet-ef
    and cannot migrate.
  EOT
  type        = string
}

variable "web_image" {
  description = "Fully qualified container image for the web container (src/frontend/Dockerfile: the SPA and the nginx gateway), including its tag or digest."
  type        = string
}

# --- Identity: all of it external -------------------------------------------
# Keycloak and the identity provider are NOT deployed by this configuration. Nothing below is a
# secret; the two client secrets are Secret Manager secrets the operator creates first.

variable "public_origin" {
  description = <<-EOT
    The browser-facing origin of the web container: scheme and host, no path, no trailing slash. The
    Auth service builds its redirect URI from it (`<origin>/auth/callback`), and that exact URI must
    be registered with the identity provider. Leave null to use the web service's run.app address;
    set it when a custom domain or a load balancer is in front.
  EOT
  type        = string
  default     = null

  validation {
    condition     = var.public_origin == null || can(regex("^https://[^/]+$", var.public_origin))
    error_message = "public_origin must be https://host with no path and no trailing slash."
  }
}

variable "entra_authority" {
  description = "Identity provider authority, e.g. https://login.microsoftonline.com/<tenant-id>. The v2 authorize and token endpoints are appended."
  type        = string

  validation {
    condition     = can(regex("^https://", var.entra_authority))
    error_message = "entra_authority must be an https:// URL."
  }
}

variable "entra_client_id" {
  description = "Client id of the app registration the Auth service signs users in with."
  type        = string

  validation {
    condition     = length(var.entra_client_id) > 0
    error_message = "entra_client_id must be set."
  }
}

variable "entra_scopes" {
  description = "Scopes requested at sign-in. Must include offline_access. The default makes the access token a Microsoft Graph token, which Keycloak's oidc provider can validate."
  type        = string
  default     = "openid profile offline_access User.Read"

  validation {
    condition     = contains(split(" ", var.entra_scopes), "offline_access")
    error_message = "entra_scopes must include offline_access: sessions cannot refresh without it."
  }
}

variable "entra_request_own_client_id_scope" {
  description = "Also request the app's own client id as a scope. False for Entra ID; true only for Azure AD B2C."
  type        = bool
  default     = false
}

variable "entra_client_secret_name" {
  description = "Secret Manager secret id holding the app registration's client secret. Create it first. Null means <environment prefix>-entra-client-secret."
  type        = string
  default     = null
}

variable "keycloak_base_url" {
  description = "Base URL of the existing Keycloak server, e.g. https://keycloak.example.com. No /realms/... suffix."
  type        = string

  validation {
    condition     = can(regex("^https://", var.keycloak_base_url))
    error_message = "keycloak_base_url must be an https:// URL."
  }
}

variable "keycloak_realm" {
  description = "Keycloak realm that holds this portal's client."
  type        = string
}

variable "keycloak_issuer" {
  description = "Exact `iss` of the tokens Keycloak mints. Null means <keycloak_base_url>/realms/<keycloak_realm>. Set it when Keycloak sits behind a gateway that serves a different address than the one it stamps into tokens."
  type        = string
  default     = null
}

variable "keycloak_jwks_url" {
  description = "Keycloak JWKS endpoint the API fetches signing keys from (https). Null means <issuer>/protocol/openid-connect/certs."
  type        = string
  default     = null

  validation {
    condition     = var.keycloak_jwks_url == null || can(regex("^https://", var.keycloak_jwks_url))
    error_message = "keycloak_jwks_url must be an https:// URL."
  }
}

variable "keycloak_portal_client_id" {
  description = "This portal's Keycloak client id. The Auth service exchanges tokens to it and the API accepts only tokens minted for it; one value feeds both."
  type        = string

  validation {
    condition     = length(var.keycloak_portal_client_id) > 0
    error_message = "keycloak_portal_client_id must be set."
  }
}

variable "keycloak_subject_issuer" {
  description = "Alias of the Keycloak identity provider that trusts the sign-in token (subject_issuer of the token exchange). Empty is refused by the Auth service outside Development."
  type        = string
  default     = "oidc"

  validation {
    condition     = length(var.keycloak_subject_issuer) > 0
    error_message = "keycloak_subject_issuer must not be empty outside Development."
  }
}

variable "keycloak_client_secret_name" {
  description = "Secret Manager secret id holding the portal client's secret. Create it first. Null means <environment prefix>-keycloak-client-secret."
  type        = string
  default     = null
}

variable "auth_admin_api_key_enabled" {
  description = "Create an admin API key and enable POST /internal/admin/users/{subject}/refresh on the Auth service. Off by default: the gateway answers 404 for /internal/, so only a caller holding IAM access to the service could reach it."
  type        = bool
  default     = false
}

# --- Sizing -----------------------------------------------------------------

variable "sql_tier" {
  description = "Cloud SQL machine tier. SQL Server requires at least 3.75 GB of memory."
  type        = string
  default     = "db-custom-2-7680"
}

variable "sql_disk_size_gb" {
  description = "Cloud SQL data disk size in GB. Autoresize is enabled, so this is a floor."
  type        = number
  default     = 20
}

variable "sql_database_name" {
  description = "Application database name."
  type        = string
  default     = "app"
}

variable "sql_user" {
  description = "Application SQL login. Its password is generated and stored in Secret Manager."
  type        = string
  default     = "app_user"
}

variable "auth_sql_database_name" {
  description = "Auth service database name."
  type        = string
  default     = "auth"
}

variable "auth_sql_user" {
  description = "Auth service SQL login. Its password is generated and stored in Secret Manager."
  type        = string
  default     = "auth_user"
}

variable "redis_memory_size_gb" {
  description = "Memorystore capacity in GB."
  type        = number
  default     = 1
}

variable "api_min_instances" {
  description = "Minimum Cloud Run instances. Above zero avoids cold starts and costs money."
  type        = number
  default     = 0
}

variable "api_max_instances" {
  description = "Maximum Cloud Run instances."
  type        = number
  default     = 4
}

variable "api_cpu" {
  description = "CPU limit per Cloud Run instance."
  type        = string
  default     = "1"
}

variable "api_memory" {
  description = "Memory limit per Cloud Run instance."
  type        = string
  default     = "512Mi"
}

variable "auth_min_instances" {
  description = "Minimum Auth service instances. Every gated request calls it, so a cold start is a slow first page; raise to 1 for prod."
  type        = number
  default     = 0
}

variable "auth_max_instances" {
  description = "Maximum Auth service instances. Sessions live in SQL and Redis, so instances are interchangeable."
  type        = number
  default     = 4
}

variable "auth_cpu" {
  description = "CPU limit per Auth service instance."
  type        = string
  default     = "1"
}

variable "auth_memory" {
  description = "Memory limit per Auth service instance."
  type        = string
  default     = "512Mi"
}

variable "web_min_instances" {
  description = "Minimum web instances. Keep at least 1: the gateway refreshes its Cloud Run ID tokens with a timer, and an instance that scaled to zero has none until it is ready again."
  type        = number
  default     = 1

  validation {
    condition     = var.web_min_instances >= 1
    error_message = "web_min_instances must be at least 1 (see the description)."
  }
}

variable "web_max_instances" {
  description = "Maximum web instances."
  type        = number
  default     = 4
}

variable "web_cpu" {
  description = "CPU limit per web instance."
  type        = string
  default     = "1"
}

variable "web_memory" {
  description = "Memory limit per web instance."
  type        = string
  default     = "256Mi"
}

variable "web_allow_unauthenticated" {
  description = "Grant allUsers the invoker role on the web service so browsers can reach it. The gateway authenticates every gated request itself. Set false where policy forbids allUsers, and front the service with a load balancer."
  type        = bool
  default     = true
}

# --- Networking -------------------------------------------------------------

variable "network_name" {
  description = "VPC network hosting the private service connection and the connector."
  type        = string
  default     = "default"
}

variable "connector_cidr" {
  description = <<-EOT
    /28 CIDR reserved for the Serverless VPC Access connector. Must not overlap any
    existing subnet in the network, and each environment needs its own range.
  EOT
  type        = string
  default     = "10.8.0.0/28"
}

variable "labels" {
  description = "Labels applied to every resource that supports them."
  type        = map(string)
  default     = {}
}
