// Runtime configuration, read before the app bootstraps. In a container the nginx entrypoint
// (docker/40-env.sh) rewrites this file from environment variables; locally (nx serve, tests)
// these defaults keep the dev role picker.
//
// NEVER commit real client ids, tenant names, or authority URLs here.
window.__APP_AUTH_MODE__ = 'dev';
window.__APP_OIDC_CONFIG__ = { clientId: '', authority: '', scope: '' };
