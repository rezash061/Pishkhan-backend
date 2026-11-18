# syntax=docker/dockerfile:1
#
# Pishkhan.Ingress: webhook ingress (verifies, dedupes, produces to Kafka; no database access).
#
# Build from the backend/ folder, which is the build context:
#   docker build -f src/Hosts/Pishkhan.Ingress/Dockerfile -t pishkhan/ingress:dev .
#
# Produces the same image as `dotnet publish -c Release -t:PublishContainer` (ADR 0014):
# noble-chiseled-extra base (ICU + tzdata for Persian formatting and Asia/Tehran), non-root `app` user,
# no shell, HTTP on port 8080.

ARG DOTNET_VERSION=10.0

# ---- build ---------------------------------------------------------------------------------------------
# The SDK runs on the build machine's platform and cross-publishes for the target one (amd64 or arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG TARGETARCH
WORKDIR /src

# Repo-wide build settings and central package versions.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY config/ config/
COPY src/ src/

ARG BUILD_CONFIGURATION=Release
# Restoring for one Linux architecture keeps other platforms' native libraries (Windows, macOS, ARM, ...) out of the image.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/Hosts/Pishkhan.Ingress/Pishkhan.Ingress.csproj --os linux -a $TARGETARCH

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Hosts/Pishkhan.Ingress/Pishkhan.Ingress.csproj \
        -c ${BUILD_CONFIGURATION} \
        --os linux -a $TARGETARCH \
        --self-contained false \
        --no-restore \
        -p:UseAppHost=false \
        -o /app/publish

# ---- runtime -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS final
WORKDIR /app

LABEL org.opencontainers.image.source="https://github.com/rezash061/Pishkhan-backend" \
      org.opencontainers.image.title="pishkhan-ingress"

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    TZ=UTC

COPY --from=build --chown=app:app /app/publish .

USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Pishkhan.Ingress.dll"]
