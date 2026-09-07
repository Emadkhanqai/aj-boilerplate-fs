# Static SPA build. `apps/web`'s `production` configuration sets outputMode: "static" with
# ssr/prerender disabled, so the runtime image is just nginx serving the built browser bundle.
FROM node:26-alpine AS build
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
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/40-env.sh /docker-entrypoint.d/40-env.sh
RUN chmod +x /docker-entrypoint.d/40-env.sh
EXPOSE 80
