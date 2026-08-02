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
