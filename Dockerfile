# The server as a container image, built by .github/workflows/image.yml through the shared mcp-image.yml and pushed
# from a version tag as ghcr.io/<owner>/s3l-connector-mcp.
#
#   docker build -t s3l-connector-mcp .
#
# Snail.Sdk.Mcp.Http listens on loopback only, which a container's port does not reach: to serve HTTP from the image,
# the server gives it a host of its own that listens on every interface, with SNAIL_MCP_CONNECTOR_URL and EXPOSE here.
#
# The SDK is pinned to the band global.json asks for: on the floating sdk:10.0 a newer feature band would one day
# refuse the build.
# The server is framework-dependent: published without Native AOT onto the ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/Snail.MCP.Connector.csproj -c Release -o /app -p:PublishAot=false -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER app

ENTRYPOINT ["dotnet", "/app/Snail.MCP.Connector.dll"]
