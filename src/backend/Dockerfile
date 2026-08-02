# Multi-stage build for AjBoilerplate.Api. The API csproj's ProjectReferences (Application/
# Infrastructure/Contracts) resolve via relative paths, so a full source COPY before publish is
# required.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AjBoilerplate.Api/AjBoilerplate.Api.csproj -c Release -o /app/publish

# dotnet-ef, kept only in this stage — used by the `migrate` compose service (target: build) to
# apply EF Core migrations against the Dockerized database before `api` starts. Not shipped in
# `runtime`: production migrations are a controlled release step, never an app-startup side effect.
RUN dotnet tool install --global dotnet-ef --version 10.*
ENV PATH="$PATH:/root/.dotnet/tools"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as the non-root user the aspnet image already ships.
USER $APP_UID

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AjBoilerplate.Api.dll"]
