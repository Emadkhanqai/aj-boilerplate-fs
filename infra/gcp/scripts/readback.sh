#!/usr/bin/env bash
# Reads a deployed Cloud Run service back and fails when it is not what the manifest says.
#
# The apply log says what was SENT. Only the platform says what it KEPT: a value can be normalised,
# dropped or applied to a revision that never receives traffic. So deploy-environment.yml runs this
# against the live service after every apply, and its output is the quotable record.
#
#   readback.sh service <name> <expected-ingress> <expected-egress|none> <revision-digest> [<pushed-digest>]
#       reads `gcloud run services describe --format=json` on stdin
#   readback.sh iam <name>
#       reads `gcloud run services get-iam-policy --format=json` on stdin; fails if anyone anonymous
#       can invoke the service
#   readback.sh self-test
#
# Field paths are those of the Knative-style resource `gcloud run services describe` prints. Ingress
# lives on the SERVICE metadata and egress/maxScale on the revision TEMPLATE, which is why a wrong
# path and an unset value look identical: an empty read is a finding, never a pass.
set -uo pipefail

fail() { echo "::error::$*"; exit 1; }

service() {
  local name=$1 want_ingress=$2 want_egress=$3 digest=$4 pushed=${5:-}
  local json ingress egress max image connector
  json=$(cat)
  [ -n "$json" ] || fail "$name: describe returned nothing."

  ingress=$(jq -r '.metadata.annotations["run.googleapis.com/ingress"] // "<unset>"' <<<"$json")
  egress=$(jq -r '.spec.template.metadata.annotations["run.googleapis.com/vpc-access-egress"] // "<unset>"' <<<"$json")
  connector=$(jq -r '.spec.template.metadata.annotations["run.googleapis.com/vpc-access-connector"] // "<unset>"' <<<"$json")
  max=$(jq -r '.spec.template.metadata.annotations["autoscaling.knative.dev/maxScale"] // "<unset>"' <<<"$json")
  image=$(jq -r '.spec.template.spec.containers[0].image // "<unset>"' <<<"$json")

  echo "$name: ingress=$ingress egress=$egress connector=$connector maxScale=$max"
  echo "$name: image=$image"
  echo "$name: revision-digest=${digest:-<unset>}"

  [ "$ingress" = "$want_ingress" ] || fail "$name: ingress is '$ingress', expected '$want_ingress'."
  if [ "$want_egress" = none ]; then
    [ "$connector" = "<unset>" ] || fail "$name: has a VPC connector ('$connector'); it should have none."
  else
    [ "$egress" = "$want_egress" ] || fail "$name: egress is '$egress', expected '$want_egress'."
  fi
  [ "$max" != "<unset>" ] || fail "$name: maxScale is unset."
  case "$digest" in sha256:* | *@sha256:*) ;; *) fail "$name: the running revision reports no image digest." ;; esac
  if [ -n "$pushed" ]; then
    [ "${digest##*@}" = "${pushed##*@}" ] \
      || fail "$name: running digest ${digest##*@} is not the one pushed by this run (${pushed##*@})."
    echo "$name: running digest equals the digest pushed by this run."
  fi
}

iam() {
  local name=$1 json members anonymous
  json=$(cat)
  [ -n "$json" ] || fail "$name: get-iam-policy returned nothing."
  members=$(jq -r '[.bindings[]? | select(.role=="roles/run.invoker") | .members[]] | sort | join(",")' <<<"$json")
  anonymous=$(jq -r '[.bindings[]? | select(.role=="roles/run.invoker") | .members[] | select(.=="allUsers" or .=="allAuthenticatedUsers")] | length' <<<"$json")
  echo "$name: roles/run.invoker -> ${members:-<nobody>}"
  [ "$anonymous" = 0 ] || fail "$name: allUsers or allAuthenticatedUsers can invoke it; it must be reachable only by the gateway."
}

self_test() {
  local ok bad
  ok='{"metadata":{"annotations":{"run.googleapis.com/ingress":"all"}},"spec":{"template":{"metadata":{"annotations":{"run.googleapis.com/vpc-access-egress":"private-ranges-only","run.googleapis.com/vpc-access-connector":"c","autoscaling.knative.dev/maxScale":"4"}},"spec":{"containers":[{"image":"r/auth:abc"}]}}}}'
  printf '%s' "$ok" | service t all private-ranges-only "r/auth@sha256:aa" "sha256:aa" > /dev/null || { echo "self-test: positive control failed"; exit 1; }
  # Each of these must be REFUSED. A readback that cannot say no proves nothing.
  ( printf '%s' "$ok" | service t internal private-ranges-only "r/auth@sha256:aa" ) > /dev/null 2>&1 && { echo "self-test: wrong ingress accepted"; exit 1; }
  ( printf '%s' "$ok" | service t all all-traffic "r/auth@sha256:aa" ) > /dev/null 2>&1 && { echo "self-test: wrong egress accepted"; exit 1; }
  ( printf '%s' "$ok" | service t all private-ranges-only "" ) > /dev/null 2>&1 && { echo "self-test: empty digest accepted"; exit 1; }
  ( printf '%s' "$ok" | service t all private-ranges-only "r/auth@sha256:aa" "sha256:bb" ) > /dev/null 2>&1 && { echo "self-test: digest mismatch accepted"; exit 1; }
  ( printf '%s' "$ok" | service t all none "r/auth@sha256:aa" ) > /dev/null 2>&1 && { echo "self-test: unexpected connector accepted"; exit 1; }
  ( printf '{}' | service t all private-ranges-only "r/auth@sha256:aa" ) > /dev/null 2>&1 && { echo "self-test: empty resource accepted"; exit 1; }
  bad='{"bindings":[{"role":"roles/run.invoker","members":["serviceAccount:w@p.iam.gserviceaccount.com","allUsers"]}]}'
  ok='{"bindings":[{"role":"roles/run.invoker","members":["serviceAccount:w@p.iam.gserviceaccount.com"]}]}'
  printf '%s' "$ok" | iam t > /dev/null || { echo "self-test: iam positive control failed"; exit 1; }
  ( printf '%s' "$bad" | iam t ) > /dev/null 2>&1 && { echo "self-test: allUsers accepted"; exit 1; }
  echo "self-test: 1 positive and 6 negative service checks, 1 positive and 1 negative iam check behaved."
}

cmd=${1:-}
shift || true
case "$cmd" in
  service) service "$@" ;;
  iam) iam "$@" ;;
  self-test) self_test ;;
  *) echo "usage: readback.sh service|iam|self-test ..." >&2; exit 2 ;;
esac
