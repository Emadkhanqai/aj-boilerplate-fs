#!/bin/sh
# Picks which nginx configuration this container runs, BEFORE the official image's
# 20-envsubst-on-templates.sh renders /etc/nginx/templates/*.template into conf.d.
#
#   AUTH_MODE=dev                   -> dev       static SPA, /api/ proxied UNGATED (local role picker only)
#   AUTH_PROVIDER=embedded (default)-> embedded  the full gateway: auth_request against our Auth service
#   AUTH_PROVIDER=usp               -> usp       static SPA only; USP's shared gateway does the rest
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
echo "16-nginx-profile.sh: using nginx profile '${profile}'"
