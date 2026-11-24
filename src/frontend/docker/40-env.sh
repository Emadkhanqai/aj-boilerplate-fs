#!/bin/sh
# Rewrites the SPA's runtime config (env.js) from container env vars, before nginx starts serving.
# The value lands inside a single-quoted JS string literal below; a mode keyword can legitimately
# contain no quotes, backslashes or newlines, so those are stripped outright rather than escaped —
# a crafted value must not be able to break out of its literal in a script that runs before the app
# bootstraps.
#
# The SPA needs ONE setting:
#   AUTH_MODE   gateway (default) | dev
# AUTH_PROVIDER (embedded | usp) is for nginx, not the browser: see 16-nginx-profile.sh. There are
# no client ids, authorities or scopes here any more — the browser performs no identity-provider
# interaction, and the Auth service owns every one of those values.
set -e
sanitize() { printf '%s' "$1" | tr -d "'\\\\\n\r"; }
AUTH_MODE_S=$(sanitize "${AUTH_MODE:-gateway}")
case "$AUTH_MODE_S" in
  gateway|dev) ;;
  *) echo "40-env.sh: AUTH_MODE must be 'gateway' or 'dev'. Refusing to start." >&2; exit 1 ;;
esac
cat > /usr/share/nginx/html/env.js <<EOJS
window.__APP_AUTH_MODE__ = '${AUTH_MODE_S}';
EOJS
