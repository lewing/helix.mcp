# Multi-stage build for hlx — produces a minimal stdio MCP server image
# suitable for MCP gateways that launch servers as containerized stdio
# subprocesses (e.g. GitHub Agentic Workflows' MCP Gateway).
#
# hlx auto-dispatches to the `mcp` subcommand when invoked with no args
# and stdin is redirected (see src/HelixTool/Program.cs), so the
# ENTRYPOINT can stay bare. An MCP gateway connecting over stdio gets
# the stdio MCP server transparently.

ARG DOTNET_VERSION=10.0

# ---------- Stage 1: build & install the tool from source ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Copy manifests first so layer caching survives source-only changes.
# README.md is required by src/HelixTool/HelixTool.csproj for `dotnet pack`
# (it sets <Content Include="..\..\README.md" Pack="true" ... />).
COPY HelixTool.slnx Directory.Packages.props nuget.config README.md LICENSE ./
COPY src/ ./src/

ARG VERSION=0.0.0-local
RUN dotnet pack src/HelixTool -c Release -o /pkg \
      /p:Version=${VERSION} \
      /p:ContinuousIntegrationBuild=true \
 && dotnet tool install \
      --tool-path /tools \
      --add-source /pkg \
      --version ${VERSION} \
      lewing.helix.mcp

# ---------- Stage 2: minimal runtime image ----------
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}

LABEL org.opencontainers.image.source="https://github.com/lewing/helix.mcp"
LABEL org.opencontainers.image.description="hlx — CLI and stdio MCP server for investigating .NET CI failures in Helix and Azure DevOps"
LABEL org.opencontainers.image.licenses="MIT"

COPY --from=build /tools /usr/local/bin/

# Cache lives under the user's home by default; create a writable home
# so non-root containers in MCP gateways can still open the SQLite cache.
ENV HOME=/home/hlx \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /home/hlx && chmod 0777 /home/hlx

ENTRYPOINT ["hlx"]
