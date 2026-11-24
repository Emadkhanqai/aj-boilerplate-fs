# ---------------------------------------------------------------------------
# The web container: the SPA, and the nginx gateway in front of the API and the Auth service
# (AUTH_MODE=gateway, AUTH_PROVIDER=embedded; see docs/auth/gateway-contract.md).
#
# This is the only service a browser reaches. It serves the SPA, sends every gated request through
# `auth_request` to the Auth service, and forwards `/api/` to the API with the Keycloak JWT the
# Auth service returns.
#
# WHO MAY CALL THE API AND THE AUTH SERVICE
#
# Nobody but this service's identity. Both are Cloud Run services whose invoker list is exactly
# `web`'s service account, so a request without a Google ID token for that identity is answered 403
# by Cloud Run before the application sees it. They are not on the internet in any useful sense,
# even though their `.run.app` addresses are.
#
# The gateway proves who it is with an ID token per target, sent as `X-Serverless-Authorization`
# (docker/njs/token.js). `Authorization` is not used for that: the API needs it for the Keycloak JWT.
#
# Why IAM and not `ingress = internal`: a Cloud Run service reaches another one's `.run.app` address
# over Google's public front end, which `internal` ingress refuses unless the caller's egress is
# routed through the VPC with Private Google Access. That needs ALL_TRAFFIC egress on the gateway
# and a subnet setting this configuration does not own. IAM needs neither and fails closed. Moving
# to `internal` ingress as well is a hardening step, not a replacement: keep the IAM binding.
# ---------------------------------------------------------------------------

resource "google_service_account" "web" {
  account_id   = "${local.name}-web"
  display_name = "Web gateway runtime service account (${var.environment})"
}

resource "google_cloud_run_v2_service_iam_member" "web_invokes_api" {
  project  = google_cloud_run_v2_service.api.project
  location = google_cloud_run_v2_service.api.location
  name     = google_cloud_run_v2_service.api.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.web.email}"
}

resource "google_cloud_run_v2_service_iam_member" "web_invokes_auth" {
  project  = google_cloud_run_v2_service.auth.project
  location = google_cloud_run_v2_service.auth.location
  name     = google_cloud_run_v2_service.auth.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.web.email}"
}

resource "google_cloud_run_v2_service" "web" {
  name                = "${local.name}-web"
  location            = var.region
  ingress             = "INGRESS_TRAFFIC_ALL"
  deletion_protection = local.is_prod
  labels              = local.labels

  template {
    service_account = google_service_account.web.email
    labels          = local.labels

    scaling {
      min_instance_count = var.web_min_instances
      max_instance_count = var.web_max_instances
    }

    # No VPC connector: the gateway reaches only the two run.app addresses and the metadata server.

    containers {
      image = var.web_image

      # nginx listens on 80 (docker/nginx/embedded.conf.template).
      ports {
        container_port = 80
      }

      resources {
        limits = {
          cpu    = var.web_cpu
          memory = var.web_memory
        }

        # CPU stays allocated between requests. The ID tokens are refreshed by a timer inside nginx,
        # and a throttled instance does not run timers: after an idle hour its tokens would have
        # expired and the first request back would be answered 403 by Cloud Run IAM, which reads as
        # "not signed in". This is what that costs: the instance is billed while idle.
        cpu_idle = false
      }

      env {
        name  = "AUTH_MODE"
        value = "gateway"
      }

      env {
        name  = "AUTH_PROVIDER"
        value = "embedded"
      }

      env {
        name  = "UPSTREAM_AUTH"
        value = "cloud-run-id-token"
      }

      # The audience of each ID token is this address, so it is the same value, not a copy of it.
      env {
        name  = "API_UPSTREAM"
        value = google_cloud_run_v2_service.api.uri
      }

      env {
        name  = "AUTH_UPSTREAM"
        value = google_cloud_run_v2_service.auth.uri
      }

      # Ready only once both ID tokens exist (see docker/njs/token.js).
      startup_probe {
        initial_delay_seconds = 5
        period_seconds        = 5
        failure_threshold     = 24

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
}

# The browser's entry point. nginx authenticates every gated request itself, so Cloud Run must let
# anonymous callers reach it. If your organisation forbids `allUsers`, set web_allow_unauthenticated
# to false and put a load balancer in front instead (README.md).
resource "google_cloud_run_v2_service_iam_member" "web_public" {
  count = var.web_allow_unauthenticated ? 1 : 0

  project  = google_cloud_run_v2_service.web.project
  location = google_cloud_run_v2_service.web.location
  name     = google_cloud_run_v2_service.web.name
  role     = "roles/run.invoker"
  member   = "allUsers"
}
