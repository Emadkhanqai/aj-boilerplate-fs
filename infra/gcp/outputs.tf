# Nothing sensitive is output. The database password and the assembled connection
# string live in Secret Manager and are never surfaced here — an output ends up in
# CI logs, in `terraform output`, and in anyone's shell history.

output "web_url" {
  description = "Where users go: the web container. Register <this URL>/auth/callback as the redirect URI with the identity provider (or <public_origin>/auth/callback when public_origin is set)."
  value       = google_cloud_run_v2_service.web.uri
}

output "public_origin" {
  description = "The origin the Auth service was told to use. It must equal the origin users reach the site at, or sign-in returns to the wrong address."
  value       = local.public_origin
}

output "auth_url" {
  description = "Address of the Auth service. IAM-protected: only the web service account may call it."
  value       = google_cloud_run_v2_service.auth.uri
}

output "auth_migrate_job" {
  description = "Cloud Run job that applies the Auth service's migrations. Execute it before rolling out a new Auth image."
  value       = google_cloud_run_v2_job.auth_migrate.name
}

output "web_service_account_email" {
  description = "Identity of the gateway. It is the only caller allowed on the API and the Auth service."
  value       = google_service_account.web.email
}

output "api_url" {
  description = "Address of the API's Cloud Run service. IAM-protected: only the web service account may call it."
  value       = google_cloud_run_v2_service.api.uri
}

output "api_service_account_email" {
  description = "Runtime identity of the API. Grant it access to anything else it needs."
  value       = google_service_account.api.email
}

output "artifact_registry_repository" {
  description = "Docker repository to push the API image to."
  value       = "${var.region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.containers.repository_id}"
}

output "sql_instance_connection_name" {
  description = "Cloud SQL instance connection name, for the auth proxy during local debugging."
  value       = google_sql_database_instance.main.connection_name
}

output "redis_host" {
  description = "Private Memorystore endpoint. Reachable from inside the VPC only."
  value       = "${google_redis_instance.cache.host}:${google_redis_instance.cache.port}"
}

output "db_connection_secret_id" {
  description = "Secret Manager secret holding the connection string."
  value       = google_secret_manager_secret.db_connection.secret_id
}

output "auth_db_connection_secret_id" {
  description = "Secret Manager secret holding the Auth service's connection string."
  value       = google_secret_manager_secret.auth_db_connection.secret_id
}

output "ci_service_account" {
  description = "Value for the GCP_SERVICE_ACCOUNT repository secret."
  value       = google_service_account.ci.email
}

output "workload_identity_provider" {
  description = "Value for the GCP_WORKLOAD_IDENTITY_PROVIDER repository secret."
  value       = google_iam_workload_identity_pool_provider.github.name
}
