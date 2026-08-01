# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY global.json ./
COPY src/GhPrApi/GhPrApi.csproj src/GhPrApi/
COPY src/GhPrApi/packages.lock.json src/GhPrApi/
RUN dotnet restore src/GhPrApi/GhPrApi.csproj --locked-mode

COPY src/GhPrApi src/GhPrApi
RUN dotnet publish src/GhPrApi/GhPrApi.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS runtime
WORKDIR /app

# The aspnet runtime image ships no HTTP client (no curl/wget/nc), so HEALTHCHECK needs one.
# Deliberately unpinned: pinning curl to an exact apt version breaks the build as soon as
# Ubuntu supersedes it and drops the old one from the archive, and this image is rebuilt on
# every merge -- taking the current patched curl is the safer trade for a network tool.
# hadolint ignore=DL3008
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# The image runs as uid 1654 (app) and a freshly mounted volume is root-owned, so create and
# chown the cache mount point before dropping privileges. Miss this and the app silently falls
# back to the in-memory tier only.
RUN mkdir -p /data && chown app:app /data
VOLUME ["/data"]

# Point the cache at the volume by default. The code default is a relative "cache.db", which
# resolves under the read-only /app and would silently drop the image to the in-memory tier for
# anyone running the image without setting this.
ENV GitHub__CachePath=/data/cache.db

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# /health/live, not /health/ready: readiness reports GitHub reachability, and a container
# health check must not restart a working process because an upstream is down.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -fsS http://localhost:8080/health/live || exit 1

COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "GhPrApi.dll"]
