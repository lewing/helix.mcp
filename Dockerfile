# Multi-stage build for hlx — produces a minimal stdio MCP server image
# suitable for MCP gateways that launch servers as containerized stdio
# subprocesses (e.g. GitHub Agentic Workflows' MCP Gateway).
#
# hlx auto-dispatches to the `mcp` subcommand when invoked with no args
# and stdin is redirected (see src/HelixTool/Program.cs), so the
# ENTRYPOINT can stay bare. An MCP gateway connecting over stdio gets
# the stdio MCP server transparently.

# ---------- Stage 1: publish the framework-dependent binaries ----------
# Tag kept for human debuggability; the digest is what actually gets pulled.
# Future bumps via Dependabot will update both tag and digest together.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src

# Copy manifests first so layer caching survives source-only changes.
# README.md is referenced by src/HelixTool/HelixTool.csproj
# (<Content Include="..\..\README.md" Pack="true" />); LICENSE is
# kept in the image for OCI label provenance.
COPY HelixTool.slnx Directory.Packages.props nuget.config README.md LICENSE ./
COPY src/ ./src/

ARG VERSION=0.0.0-local
# `dotnet publish` (not pack) — produces a framework-dependent apphost
# that can be invoked directly. Avoids the `dotnet tool install`
# package-source-mapping conflict that the repo's nuget.config triggers
# when combined with --add-source.
RUN dotnet publish src/HelixTool \
      -c Release \
      -o /publish \
      --no-self-contained \
      /p:Version=${VERSION} \
      /p:ContinuousIntegrationBuild=true

# ---------- Stage 2: minimal runtime image ----------
# Tag kept for human debuggability; the digest is what actually gets pulled.
# Future bumps via Dependabot will update both tag and digest together.
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:6a40d375e9c8432fcf4adebae05d7e0a276e9a90dd01174df6709a090771bebc

LABEL org.opencontainers.image.source="https://github.com/lewing/helix.mcp"
LABEL org.opencontainers.image.description="hlx — CLI and stdio MCP server for investigating .NET CI failures in Helix and Azure DevOps"
LABEL org.opencontainers.image.licenses="MIT"

COPY --from=build /publish /app

# Expose the binary on PATH as `hlx` (the tool's canonical command name,
# matching `<ToolCommandName>hlx</ToolCommandName>` in HelixTool.csproj).
# On Linux the apphost resolves its own location via /proc/self/exe, so
# a symlink in /usr/local/bin/ Just Works — dependencies are still found
# in /app/.
RUN ln -s /app/HelixTool /usr/local/bin/hlx

# Cache lives under the user's home by default
# (Environment.SpecialFolder.UserProfile → $HOME on Linux per
# src/HelixTool.Core/Cache/CacheOptions.cs). Provide a writable home
# so non-root containers can still open the SQLite cache.
ENV HOME=/home/hlx \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /home/hlx && chmod 0777 /home/hlx

ENTRYPOINT ["hlx"]
