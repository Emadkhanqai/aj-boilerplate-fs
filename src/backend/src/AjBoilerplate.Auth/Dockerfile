# Multi-stage build for AjBoilerplate.Auth. The build CONTEXT is src/backend (Directory.Build.props and
# global.json live there), so build from the repository root with:
#   docker build -f src/backend/src/AjBoilerplate.Auth/Dockerfile src/backend
# The Auth service references no other project in this solution; only the context root is shared.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AjBoilerplate.Auth/AjBoilerplate.Auth.csproj -c Release -o /app/publish

# dotnet-ef, kept only in this stage: the one-off migrate job runs `--target build` and applies the
# migrations with AUTH_DB_CONNECTION set. Not shipped in `runtime`: schema changes are a controlled release
# step, never an app-startup side effect.
RUN dotnet tool install --global dotnet-ef --version 10.*
ENV PATH="$PATH:/root/.dotnet/tools"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as the non-root user the aspnet image already ships.
USER $APP_UID

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AjBoilerplate.Auth.dll"]
