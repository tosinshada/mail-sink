# syntax=docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e
#
# Mailsink server image.
#
#   docker build -f docker/Dockerfile -t mailsink .
#   docker run --rm -p 8025:8025 -p 1025:1025 -p 1143:1143 -v mailsink-data:/data mailsink
#
# Multi-platform (what CI publishes):
#
#   docker buildx build -f docker/Dockerfile --platform linux/amd64,linux/arm64 -t mailsink .
#
# Stages:
#   web      - builds the Vite/React SPA into src/Mailsink.Server/wwwroot
#   build    - publishes Mailsink.Server (framework-dependent) for the target OS/architecture
#   runtime  - chiseled ASP.NET runtime, non-root, /data volume, health check
#
# The web and build stages run on the build machine's own platform ($BUILDPLATFORM) and
# cross-compile for $TARGETOS/$TARGETARCH, so nothing runs under QEMU emulation: the
# runtime stage only copies files and has no RUN steps. Keep it that way.
#
# Base images are pinned by their multi-arch index digest: the top-level `Digest:` printed by
# `docker buildx imagetools inspect <image:tag>`, never a per-platform manifest digest. To bump
# one, re-run that command and replace the digest; the tag stays for readability only.

# ---------------------------------------------------------------------------
# 1. Web UI (runs on the build host's architecture; output is plain static files)
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:22-alpine@sha256:1314fb30ec37592d5845e5a160d522c0e680972aa534549940b8d73bde88481a AS web
WORKDIR /src/Mailsink.Web

COPY src/Mailsink.Web/package.json src/Mailsink.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/Mailsink.Web/ ./
# vite.config.ts writes to ../Mailsink.Server/wwwroot
RUN npm run build

# ---------------------------------------------------------------------------
# 2. .NET publish (cross-compiles for $TARGETOS/$TARGETARCH so multi-arch builds avoid QEMU)
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
ARG TARGETOS
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ ./src/
COPY --from=web /src/Mailsink.Server/wwwroot ./src/Mailsink.Server/wwwroot

RUN dotnet restore src/Mailsink.Server/Mailsink.Server.csproj --os "$TARGETOS" -a "$TARGETARCH" \
 && dotnet publish src/Mailsink.Server/Mailsink.Server.csproj \
      -c Release --os "$TARGETOS" -a "$TARGETARCH" --no-restore -o /out \
      -p:UseAppHost=true -p:DebugType=none

# Empty, app-owned data directory for the volume (chiseled images have no shell to mkdir/chown with).
RUN mkdir /data

# ---------------------------------------------------------------------------
# 3. Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1 AS runtime
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
