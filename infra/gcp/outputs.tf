# Nothing sensitive is output. The database password and the assembled connection
# string live in Secret Manager and are never surfaced here — an output ends up in
# CI logs, in `terraform output`, and in anyone's shell history.

output "api_url" {
  description = "Public URL of the Cloud Run service."
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

output "ci_service_account" {
  description = "Value for the GCP_SERVICE_ACCOUNT repository secret."
  value       = google_service_account.ci.email
}

output "workload_identity_provider" {
  description = "Value for the GCP_WORKLOAD_IDENTITY_PROVIDER repository secret."
  value       = google_iam_workload_identity_pool_provider.github.name
}
