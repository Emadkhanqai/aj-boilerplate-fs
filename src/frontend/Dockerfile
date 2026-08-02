# Static SPA build. `apps/web`'s `production` configuration sets outputMode: "static" with
# ssr/prerender disabled, so the runtime image is just nginx serving the built browser bundle.
FROM node:22-alpine AS build
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN npx nx build web --configuration=production

FROM nginx:alpine AS runtime
COPY --from=build /src/dist/apps/web/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/40-env.sh /docker-entrypoint.d/40-env.sh
RUN chmod +x /docker-entrypoint.d/40-env.sh
EXPOSE 80
