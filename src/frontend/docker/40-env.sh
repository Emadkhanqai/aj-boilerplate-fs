#!/bin/sh
# Rewrites the SPA's runtime auth config from container env vars, before nginx starts serving.
# The values land inside single-quoted JS string literals below; none of them (a mode keyword, a
# client id, a URL) can legitimately contain quotes, backslashes, or newlines, so those are
# stripped outright rather than escaped — a crafted value must not be able to break out of its
# literal in a script that runs before the app bootstraps.
set -e
sanitize() { printf '%s' "$1" | tr -d "'\\\\\n\r"; }
AUTH_MODE_S=$(sanitize "${AUTH_MODE:-dev}")
OIDC_CLIENT_ID_S=$(sanitize "${OIDC_CLIENT_ID:-}")
OIDC_AUTHORITY_S=$(sanitize "${OIDC_AUTHORITY:-}")
OIDC_SCOPE_S=$(sanitize "${OIDC_SCOPE:-}")
cat > /usr/share/nginx/html/env.js <<EOJS
window.__APP_AUTH_MODE__ = '${AUTH_MODE_S}';
window.__APP_OIDC_CONFIG__ = {
  clientId: '${OIDC_CLIENT_ID_S}',
  authority: '${OIDC_AUTHORITY_S}',
  scope: '${OIDC_SCOPE_S}',
};
EOJS
