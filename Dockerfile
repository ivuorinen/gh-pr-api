# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY global.json ./
COPY src/GhPrApi/GhPrApi.csproj src/GhPrApi/
COPY src/GhPrApi/packages.lock.json src/GhPrApi/
RUN dotnet restore src/GhPrApi/GhPrApi.csproj --locked-mode

COPY src/GhPrApi src/GhPrApi
RUN dotnet publish src/GhPrApi/GhPrApi.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime
WORKDIR /app

# The aspnet runtime image ships no HTTP client (no curl/wget/nc), so HEALTHCHECK needs one.
# Deliberately unpinned: pinning curl to an exact apt version breaks the build as soon as
# Ubuntu supersedes it and drops the old one from the archive, and this image is rebuilt on
# every merge -- taking the current patched curl is the safer trade for a network tool.
# hadolint ignore=DL3008
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# /health/live, not /health/ready: readiness reports GitHub reachability, and a container
# health check must not restart a working process because an upstream is down.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -fsS http://localhost:8080/health/live || exit 1

COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "GhPrApi.dll"]
