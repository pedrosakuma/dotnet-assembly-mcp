# Multi-stage Dockerfile for dotnet-assembly-mcp server (HTTP transport).
#
# Build:  docker build -t dotnet-assembly-mcp:dev -f deploy/Dockerfile .
# Run:    docker run --rm -p 8788:8080 \
#                    -v /path/to/assemblies:/assemblies:ro \
#                    dotnet-assembly-mcp:dev
#
# The server inspects .NET assemblies on disk; mount the target binaries
# read-only and point the MCP client at http://<container>:8080/mcp. For
# stdio (local tool) installs use `dotnet tool install -g dotnet-assembly-mcp`
# instead — this image is for HTTP / sidecar deployments.

ARG DOTNET_VERSION=10.0
# Version stamped onto the published assemblies. Set by the release workflow
# (`MINVER_VERSION_OVERRIDE=$VERSION`) so `serverInfo.Version` matches the image
# tag. When unset, MinVer would otherwise fall back to `0.0.0-alpha.0.0` inside
# the build container because `.dockerignore` excludes `.git` (issue #94).
ARG MINVER_VERSION_OVERRIDE=

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
ARG MINVER_VERSION_OVERRIDE

# Bring in the files needed to restore first so the layer caches well.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DotnetAssemblyMcp.Core/DotnetAssemblyMcp.Core.csproj src/DotnetAssemblyMcp.Core/
COPY src/DotnetAssemblyMcp.Server/DotnetAssemblyMcp.Server.csproj src/DotnetAssemblyMcp.Server/

RUN dotnet restore src/DotnetAssemblyMcp.Server/DotnetAssemblyMcp.Server.csproj

# Copy the rest of the source needed by the server project (and the embedded
# handoff-contract.md resource referenced from the server csproj).
COPY src/ src/
COPY docs/ docs/

RUN dotnet publish src/DotnetAssemblyMcp.Server/DotnetAssemblyMcp.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:MinVerVersionOverride=${MINVER_VERSION_OVERRIDE}

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# Kestrel listens on 8080 by default in our ASPNETCORE_URLS below.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_NOLOGO=1

# Non-root user for least privilege. The server only needs read access to the
# mounted assemblies + write access to ~/.cache/dotnet-assembly-mcp for the
# xref disk cache (HOME is /home/assemblymcp).
RUN groupadd --system --gid 10001 assemblymcp \
 && useradd --system --uid 10001 --gid assemblymcp --create-home --shell /usr/sbin/nologin assemblymcp

COPY --from=build /app/publish ./
RUN chown -R assemblymcp:assemblymcp /app
USER assemblymcp

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD dotnet DotnetAssemblyMcp.Server.dll --health-check --url=http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "DotnetAssemblyMcp.Server.dll"]
