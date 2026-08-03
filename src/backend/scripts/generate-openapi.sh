#!/usr/bin/env bash
#
# Regenerates the committed OpenAPI document at docs/api/openapi.json.
#
# The document is produced FROM THE COMPILED API, not written by hand, so it cannot describe an
# endpoint that does not exist or a shape the server does not actually serialise. The frontend
# generates its TypeScript types from this file, which is what makes it the contract rather than a
# description of one.
#
#   ./scripts/generate-openapi.sh            # rewrite the snapshot
#   ./scripts/generate-openapi.sh --check     # fail if the snapshot is out of date (what CI runs)
#
# Run it from src/backend, or from anywhere — it locates itself.
#
# WHY NO RUNNING SERVER IS NEEDED: Swashbuckle's CLI loads the built assembly and asks the same
# ISwaggerProvider the /swagger endpoint uses. Requiring a live API (and therefore a database) would
# make this ungateable in CI, and a contract gate nobody can run is not a gate.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${BACKEND_DIR}/../.." && pwd)"

SOLUTION="${BACKEND_DIR}/AjBoilerplate.slnx"
API_PROJECT="${BACKEND_DIR}/src/AjBoilerplate.Api"
SNAPSHOT="${REPO_ROOT}/docs/api/openapi.json"
DOCUMENT_NAME="v1"

CHECK_ONLY=false
if [[ "${1:-}" == "--check" ]]; then
  CHECK_ONLY=true
fi

cd "${BACKEND_DIR}"

# Development so the host boots without a real connection string or secret store. Only the service
# graph is constructed — no request is served and nothing connects to a database.
export ASPNETCORE_ENVIRONMENT=Development

echo "Restoring local tools (swagger, dotnet-ef) from .config/dotnet-tools.json..."
dotnet tool restore

echo "Building ${SOLUTION##*/} (Release)..."
dotnet build "${SOLUTION}" --configuration Release --nologo --verbosity quiet

ASSEMBLY="${API_PROJECT}/bin/Release/net10.0/AjBoilerplate.Api.dll"
if [[ ! -f "${ASSEMBLY}" ]]; then
  echo "error: expected the API assembly at ${ASSEMBLY} but it is not there." >&2
  exit 1
fi

GENERATED="$(mktemp -t openapi.XXXXXX.json)"
# shellcheck disable=SC2064  # expand GENERATED now, not at trap time.
trap "rm -f '${GENERATED}'" EXIT

echo "Generating the OpenAPI document..."
dotnet swagger tofile --output "${GENERATED}" "${ASSEMBLY}" "${DOCUMENT_NAME}" >/dev/null

# Normalise the trailing newline so the committed file is well-formed text and the byte comparison
# below does not fail on a difference no reviewer can see.
printf '\n' >>"${GENERATED}"

mkdir -p "$(dirname "${SNAPSHOT}")"

if [[ "${CHECK_ONLY}" == true ]]; then
  if [[ ! -f "${SNAPSHOT}" ]]; then
    echo "error: no committed OpenAPI document at ${SNAPSHOT}." >&2
    echo "Run ./scripts/generate-openapi.sh and commit the result." >&2
    exit 1
  fi

  if ! diff -u "${SNAPSHOT}" "${GENERATED}"; then
    cat >&2 <<'EOF'

error: the API no longer matches the committed OpenAPI document.

The diff above is a CONTRACT CHANGE. The frontend generates its types from this file, so anything
shown here changes what clients compile against — including the removals, which break them.

If the change is intended:
  1. ./scripts/generate-openapi.sh
  2. commit docs/api/openapi.json alongside the code change, so the contract change is reviewed
     rather than discovered
  3. check it against the breaking-change rules in docs/api/README.md — a removal, a rename, a
     narrowed type, a newly-required field, or a changed status code needs a new API version

If it is NOT intended, fix the API rather than regenerating the snapshot.
EOF
    exit 1
  fi

  echo "OK: the committed OpenAPI document matches the API."
  exit 0
fi

cp "${GENERATED}" "${SNAPSHOT}"
echo "Wrote ${SNAPSHOT}"
