# The server as a container image, built by .github/workflows/image.yml through the shared mcp-image.yml and pushed
# from a version tag as ghcr.io/<owner>/s3l-connector-mcp.
#
#   docker build --build-context feed=../../nuget -t s3l-connector-mcp:local .
#
# Snail.Sdk is taken from the author's local feed for now: the build takes it as a build context named feed, and
# restore finds it under the name nuget.config maps Snail.Sdk to.
#
# The server listens on every interface at 8830 for the platform's workers. The SDK is pinned to the band global.json
# asks for: on the floating sdk:10.0 a newer feature band would one day refuse the build. The server is
# framework-dependent: published without Native AOT onto the ASP.NET runtime.
#
# VERSION is the release's when the shared mcp-image.yml builds the image (0.0.0-<branch>.<commit> from a branch)
# and 0.0.0-local otherwise: the server reports it in initialize and the image carries it as its OCI version.
ARG VERSION=0.0.0-local

FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ARG VERSION
WORKDIR /source
COPY --from=feed . /feed
RUN dotnet nuget add source /feed --name LocalPackages
COPY . .
RUN dotnet publish src/Snail.MCP.Connector.csproj -c Release -o /app -p:PublishAot=false -p:UseAppHost=false -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION
LABEL org.opencontainers.image.version=$VERSION
# tini runs as process 1 and starts the program under it: .NET ends an unhandled exception with SIGABRT, which process
# 1 of a container ignores, so run as process 1 itself a program that fails at start spins at a full core and never
# exits, and nothing restarts it. Under tini it exits with 134.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends tini \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The journal of queries lives in the server's data folder, a volume on the stand: made here so the volume takes the app user's ownership.
RUN mkdir -p /home/app/.s3l-mcp-connector && chown -R app:app /home/app
ENV ASPNETCORE_HTTP_PORTS=8830 \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft.AspNetCore=Warning
EXPOSE 8830
USER app

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "/app/Snail.MCP.Connector.dll"]
