# The server as a container image, built by .github/workflows/image.yml through the shared mcp-image.yml and pushed
# from a version tag as ghcr.io/<owner>/s3l-connector-mcp.
#
#   docker build --build-context feed=../../nuget -t snail/s3l-connector-mcp .
#
# Snail.Sdk is not on nuget.org yet: the build takes the author's local feed as a build context named feed, and restore
# finds it under the name nuget.config maps Snail.Sdk to.
#
# The server listens on every interface at 8830 for the platform's workers. The SDK is pinned to the band global.json
# asks for: on the floating sdk:10.0 a newer feature band would one day refuse the build. The server is
# framework-dependent: published without Native AOT onto the ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /source
COPY --from=feed . /feed
RUN dotnet nuget add source /feed --name LocalPackages
COPY . .
RUN dotnet publish src/Snail.MCP.Connector.csproj -c Release -o /app -p:PublishAot=false -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The journal of queries lives in the server's data folder, a volume on the stand: made here so the volume takes the app user's ownership.
RUN mkdir -p /home/app/.s3l-mcp-connector && chown -R app:app /home/app
ENV ASPNETCORE_HTTP_PORTS=8830 \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft.AspNetCore=Warning
EXPOSE 8830
USER app

ENTRYPOINT ["dotnet", "/app/Snail.MCP.Connector.dll"]
