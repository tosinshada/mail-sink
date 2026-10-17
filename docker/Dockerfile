# syntax=docker/dockerfile:1.7
#
# Mailsink server image.
#
#   docker build -f docker/Dockerfile -t mailsink .
#   docker run --rm -p 8025:8025 -p 1025:1025 -p 1143:1143 -v mailsink-data:/data mailsink
#
# Stages:
#   web      - builds the Vite/React SPA into src/Mailsink.Server/wwwroot
#   build    - publishes Mailsink.Server (framework-dependent) for the target architecture
#   runtime  - chiseled ASP.NET runtime, non-root, /data volume, health check

# ---------------------------------------------------------------------------
# 1. Web UI (runs on the build host's architecture; output is plain static files)
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /src/Mailsink.Web

COPY src/Mailsink.Web/package.json src/Mailsink.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/Mailsink.Web/ ./
# vite.config.ts writes to ../Mailsink.Server/wwwroot
RUN npm run build

# ---------------------------------------------------------------------------
# 2. .NET publish (cross-compiles for $TARGETARCH so multi-arch builds avoid QEMU)
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ ./src/
COPY --from=web /src/Mailsink.Server/wwwroot ./src/Mailsink.Server/wwwroot

RUN dotnet restore src/Mailsink.Server/Mailsink.Server.csproj -a "$TARGETARCH" \
 && dotnet publish src/Mailsink.Server/Mailsink.Server.csproj \
      -c Release -a "$TARGETARCH" --no-restore -o /out \
      -p:UseAppHost=true -p:DebugType=none

# Empty, app-owned data directory for the volume (chiseled images have no shell to mkdir/chown with).
RUN mkdir /data

# ---------------------------------------------------------------------------
# 3. Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /out ./
COPY --from=build --chown=$APP_UID:$APP_UID /data /data

ENV MAILSINK__DataDir=/data \
    MAILSINK__BindAddress=0.0.0.0 \
    MAILSINK__Http__Urls=http://0.0.0.0:8025 \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_EnableDiagnostics=0

VOLUME /data

# HTTP/UI, SMTP capture, SMTP submission, IMAP, IMAPS, POP3 (POP3 is off by default: MAILSINK__Pop3__Enabled=true)
EXPOSE 8025 1025 1587 1143 1993 1110

USER $APP_UID

HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=3 \
  CMD ["/app/mailsink", "health"]

ENTRYPOINT ["/app/mailsink"]
