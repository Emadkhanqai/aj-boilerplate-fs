#!/usr/bin/env bash
# Builds a throwaway nginx image with the gateway's own config files and
#   1. runs `nginx -t` for every profile the entrypoint can pick (embedded with both upstream modes, usp, dev);
#   2. starts the embedded profile in front of a stub upstream that sends its own Content-Security-Policy and checks that
#      /auth/handoff carries EXACTLY ONE CSP, byte-equal to the upstream's, while /auth/login still carries the gateway's.
# Needs docker; touches only containers and an image it creates and removes. Run from anywhere: bash src/frontend/docker/check-gateway-config.sh
set -u
here="$(cd "$(dirname "$0")" && pwd)"
ctx="$(dirname "$here")"
img="ajbp-gateway-check:$$"
net="ajbp-gateway-check-$$"
fail=0
note() { printf '%s\n' "$*"; }
bad() { note "FAIL: $*"; fail=1; }
cleanup() {
  docker rm -f "$net-gw" "$net-stub" > /dev/null 2>&1
  docker network rm "$net" > /dev/null 2>&1
  docker rmi -f "$img" > /dev/null 2>&1
}
trap cleanup EXIT

docker build -q -t "$img" -f - "$ctx" > /dev/null <<'DOCKERFILE' || { note "FAIL: image build"; exit 1; }
FROM nginx:alpine
RUN mkdir -p /usr/share/nginx/html && echo ok > /usr/share/nginx/html/index.html
COPY docker/nginx/embedded.conf.template docker/nginx/usp.conf.template docker/nginx/dev.conf.template /etc/nginx/profiles/
COPY docker/nginx/snippets/ /etc/nginx/snippets/
COPY docker/nginx/upstream/ /etc/nginx/upstream-profiles/
COPY docker/njs/token.js /etc/nginx/njs/token.js
COPY docker/nginx/pages/gateway-pages.css /usr/share/nginx/gateway/gateway-pages.css
COPY docker/16-nginx-profile.sh /docker-entrypoint.d/16-nginx-profile.sh
COPY docker/40-env.sh /docker-entrypoint.d/40-env.sh
RUN chmod +x /docker-entrypoint.d/16-nginx-profile.sh /docker-entrypoint.d/40-env.sh
ENV AUTH_MODE=gateway AUTH_PROVIDER=embedded API_UPSTREAM=http://stub:8080 AUTH_UPSTREAM=http://stub:8080 \
    NGINX_ENVSUBST_FILTER="^(API_UPSTREAM|AUTH_UPSTREAM)$"
DOCKERFILE

check_t() { # label, env...
  local label="$1" out; shift
  out="$(mktemp)"
  local envs=(); for e in "$@"; do envs+=(-e "$e"); done
  docker run --rm "${envs[@]}" --add-host stub:127.0.0.1 "$img" nginx -t > "$out" 2>&1; local rc=$?
  if [ $rc -eq 0 ] && grep -q "configuration file /etc/nginx/nginx.conf test is successful" "$out"; then
    note "ok   nginx -t  $label"
  else
    bad "nginx -t $label (rc=$rc)"; tail -5 "$out"
  fi
  rm -f "$out"
}
check_t "embedded / UPSTREAM_AUTH=none"            AUTH_PROVIDER=embedded
check_t "embedded / UPSTREAM_AUTH=cloud-run-id-token" AUTH_PROVIDER=embedded UPSTREAM_AUTH=cloud-run-id-token API_UPSTREAM=https://api.example.test AUTH_UPSTREAM=https://auth.example.test
check_t "usp"                                      AUTH_PROVIDER=usp
check_t "dev"                                      AUTH_MODE=dev

# ---- the hand-off CSP, against a stub that sends its own -------------------------------------------------
stub_csp="default-src 'none'; script-src 'sha256-STUBHASH'; base-uri 'none'; frame-ancestors 'none'"
docker network create "$net" > /dev/null
stubconf="$(mktemp)"
cat > "$stubconf" <<CONF
server { listen 8080; add_header Content-Security-Policy "$stub_csp" always; location / { default_type text/html; return 200 'stub'; } }
CONF
docker run -d --name "$net-stub" --network "$net" --network-alias stub -v "$stubconf:/etc/nginx/conf.d/default.conf:ro" nginx:alpine > /dev/null
docker run -d --name "$net-gw" --network "$net" -p 127.0.0.1::80 -e AUTH_PROVIDER=embedded "$img" > /dev/null
port="$(docker port "$net-gw" 80/tcp | head -1 | sed 's/.*://')"
for _ in $(seq 1 30); do curl -fs "http://127.0.0.1:$port/healthz" > /dev/null && break; sleep 0.5; done
curl -fs "http://127.0.0.1:$port/healthz" > /dev/null || bad "gateway did not start"

csp_lines() { curl -s -D - -o /dev/null "http://127.0.0.1:$port$1" | tr -d '\r' | grep -i '^content-security-policy:'; }
body_check="$(curl -s "http://127.0.0.1:$port/auth/handoff?to=x")"
[ "$body_check" = stub ] || bad "/auth/handoff did not reach the stub upstream (body: $body_check)"

handoff="$(csp_lines '/auth/handoff?to=x')"
n="$(printf '%s\n' "$handoff" | grep -c .)"
if [ "$n" = 1 ] && [ "${handoff#*: }" = "$stub_csp" ]; then
  note "ok   /auth/handoff carries exactly one CSP, equal to the upstream's"
else
  bad "/auth/handoff: $n CSP header(s): $handoff"
fi
# Control: the instrument can see duplicates. /auth/login keeps the gateway's CSP in front of the upstream's.
login="$(csp_lines '/auth/login')"
n="$(printf '%s\n' "$login" | grep -c .)"
if [ "$n" = 2 ] && printf '%s\n' "$login" | grep -q "script-src 'self'"; then
  note "ok   /auth/login still carries the gateway CSP beside the upstream's ($n headers, so duplicates are visible to this check)"
else
  bad "/auth/login: expected the gateway CSP and the upstream's, got $n: $login"
fi
for h in strict-transport-security x-content-type-options x-frame-options referrer-policy permissions-policy; do
  curl -s -D - -o /dev/null "http://127.0.0.1:$port/auth/handoff?to=x" | tr -d '\r' | grep -qi "^$h:" || bad "/auth/handoff lost $h"
done
rm -f "$stubconf"
exit $fail
