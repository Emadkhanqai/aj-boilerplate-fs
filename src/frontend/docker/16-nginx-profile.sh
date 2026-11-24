#!/bin/sh
# Picks which nginx configuration this container runs, BEFORE the official image's
# 20-envsubst-on-templates.sh renders /etc/nginx/templates/*.template into conf.d.
#
#   AUTH_MODE=dev                   -> dev       static SPA, /api/ proxied UNGATED (local role picker only)
#   AUTH_PROVIDER=embedded (default)-> embedded  the full gateway: auth_request against our Auth service
#   AUTH_PROVIDER=usp               -> usp       static SPA only; USP's shared gateway does the rest
#
# For the embedded profile, UPSTREAM_AUTH decides HOW the API and the Auth service are reached:
#   none (default)         plain proxy_pass with the browser's Host (compose, any private network)
#   cloud-run-id-token     both are Cloud Run services that require a Google ID token: nginx loads
#                          njs, fetches a token per audience from the metadata server and sends it
#                          as X-Serverless-Authorization (docker/njs/token.js). Both upstreams must
#                          be https, because the token is a credential.
#
# AUTH_MODE=dev wins: the role picker has no server session, so gating it would only redirect-loop.
# An unrecognised value REFUSES TO START rather than falling back, because the fallback for a typo in
# an auth setting would be either "no gate" or "wrong gate", and both are worse than a stopped
# container with a clear message.
set -eu

die() { echo "16-nginx-profile.sh: $*" >&2; exit 1; }

if [ "${AUTH_MODE:-gateway}" = dev ]; then
  profile=dev
else
  case "${AUTH_PROVIDER:-embedded}" in
    embedded) profile=embedded ;;
    usp) profile=usp ;;
    *) die "AUTH_PROVIDER must be 'embedded' or 'usp' (got a different value). Refusing to start." ;;
  esac
fi

# The upstreams are substituted into the template; an empty one would render a config that fails
# with "no host in upstream", which says nothing about which variable was empty.
for name in API_UPSTREAM AUTH_UPSTREAM; do
  eval "value=\${$name:-}"
  case "$profile:$name" in
    dev:AUTH_UPSTREAM|usp:AUTH_UPSTREAM|usp:API_UPSTREAM) continue ;;
  esac
  [ -n "$value" ] || die "$name is empty; profile '$profile' proxies to it. Refusing to start."
done

mkdir -p /etc/nginx/templates
cp "/etc/nginx/profiles/${profile}.conf.template" /etc/nginx/templates/default.conf.template

if [ "$profile" = embedded ]; then
  case "${UPSTREAM_AUTH:-none}" in
    none) upstream=none ;;
    cloud-run-id-token) upstream=cloud-run ;;
    *) die "UPSTREAM_AUTH must be 'none' or 'cloud-run-id-token' (got a different value). Refusing to start." ;;
  esac

  if [ "$upstream" = cloud-run ]; then
    # A bearer credential must not travel in clear text, and a typo'd scheme would do exactly that.
    for name in API_UPSTREAM AUTH_UPSTREAM; do
      eval "value=\${$name:-}"
      case "$value" in
        https://*) ;;
        *) die "UPSTREAM_AUTH=cloud-run-id-token needs $name to be an https:// URL. Refusing to start." ;;
      esac
    done
    # load_module and env are main-context directives, so they cannot live in the template, which is
    # included inside `http`. nginx wipes the environment of its workers; njs reads these.
    # `grep` first so a container restart does not stack the lines.
    if ! grep -q 'ngx_http_js_module' /etc/nginx/nginx.conf; then
      sed -i '1i load_module modules/ngx_http_js_module.so;\nenv API_UPSTREAM;\nenv AUTH_UPSTREAM;\nenv GCE_METADATA_HOST;' /etc/nginx/nginx.conf
    fi
  fi

  mkdir -p /etc/nginx/templates/upstream
  cp /etc/nginx/upstream-profiles/"${upstream}"/*.template /etc/nginx/templates/upstream/
  echo "16-nginx-profile.sh: upstream mode '${upstream}'"
fi
echo "16-nginx-profile.sh: using nginx profile '${profile}'"
