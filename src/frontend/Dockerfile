# Static SPA build. `apps/web`'s `production` configuration sets outputMode: "static" with
# ssr/prerender disabled, so the runtime image is just nginx serving the built browser bundle.
FROM node:22-alpine AS build
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN npx nx build web --configuration=production

FROM nginx:alpine AS runtime

# Patch the base image's OS packages at build time. The `nginx:alpine` tag is rebuilt on
# nginx's schedule, not Alpine's, so between those rebuilds the image ships packages for
# which Alpine has already published a fix — and the blocking Trivy scan in
# .github/workflows/supply-chain.yml fails on exactly that: a HIGH with a fix available.
# Upgrading here closes the window without pinning a base tag that goes stale, and without
# an allowlist entry, which .trivyignore.yaml is deliberately not the place for.
RUN apk upgrade --no-cache

COPY --from=build /src/dist/apps/web/browser /usr/share/nginx/html

# nginx configuration. The official image renders /etc/nginx/templates/*.template with envsubst
# into conf.d at start-up; `docker/16-nginx-profile.sh` runs just before that and copies ONE of the
# three profiles in /etc/nginx/profiles into place, chosen by AUTH_MODE / AUTH_PROVIDER.
COPY docker/nginx/embedded.conf.template docker/nginx/usp.conf.template docker/nginx/dev.conf.template /etc/nginx/profiles/
COPY docker/nginx/snippets/ /etc/nginx/snippets/
COPY docker/nginx/pages/gateway-pages.css /usr/share/nginx/gateway/gateway-pages.css
COPY docker/16-nginx-profile.sh /docker-entrypoint.d/16-nginx-profile.sh
COPY docker/40-env.sh /docker-entrypoint.d/40-env.sh
RUN chmod +x /docker-entrypoint.d/16-nginx-profile.sh /docker-entrypoint.d/40-env.sh

# Runtime settings (all overridable with `docker run -e`):
#   AUTH_MODE       gateway | dev       what the SPA does (env.js); `dev` is the role picker, local only
#   AUTH_PROVIDER   embedded | usp      who runs the gate: this container, or USP's shared gateway
#   API_UPSTREAM    where /api/ goes    (embedded and dev profiles)
#   AUTH_UPSTREAM   the Auth service    (embedded profile)
ENV AUTH_MODE=gateway \
    AUTH_PROVIDER=embedded \
    API_UPSTREAM=http://api:8080 \
    AUTH_UPSTREAM=http://auth:8080 \
    NGINX_ENVSUBST_FILTER="^(API_UPSTREAM|AUTH_UPSTREAM)$"
EXPOSE 80
