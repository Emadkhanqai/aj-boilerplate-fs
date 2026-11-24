// Runtime configuration, read before the app bootstraps. In a container the nginx entrypoint
// (docker/40-env.sh) rewrites this file from environment variables, defaulting to `gateway`;
// locally (nx serve, tests) this default keeps the dev role picker.
//
// `gateway`: the browser holds no token; nginx authenticates the session cookie (see
// docs/auth/gateway-contract.md). `dev`: role picker, local UI work and e2e only.
window.__APP_AUTH_MODE__ = 'dev';
