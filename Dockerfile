# Multi-stage build for Nexora staging on Render Free
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY Nexora.slnx ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY global.json ./
COPY src/Nexora.Business/Nexora.Business.csproj src/Nexora.Business/
COPY src/Nexora.Data/Nexora.Data.csproj src/Nexora.Data/
COPY src/Nexora.Integrations/Nexora.Integrations.csproj src/Nexora.Integrations/
COPY src/Nexora.Api/Nexora.Api.csproj src/Nexora.Api/
COPY src/Nexora.Worker/Nexora.Worker.csproj src/Nexora.Worker/
COPY tests/Nexora.UnitTests/Nexora.UnitTests.csproj tests/Nexora.UnitTests/
COPY tests/Nexora.IntegrationTests/Nexora.IntegrationTests.csproj tests/Nexora.IntegrationTests/

RUN dotnet restore Nexora.slnx

# Copy source code
COPY src/ src/

# Build EF Core migration bundle
RUN dotnet tool install --global dotnet-ef --version 10.0.8
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
    --project src/Nexora.Data \
    --startup-project src/Nexora.Api \
    --output /out/nexora-migrate \
    -r linux-x64 \
    --self-contained

# Publish Nexora.Api
RUN dotnet publish src/Nexora.Api/Nexora.Api.csproj \
    -c Release \
    -o /out/api \
    --no-restore

# Publish Nexora.Worker
RUN dotnet publish src/Nexora.Worker/Nexora.Worker.csproj \
    -c Release \
    -o /out/worker \
    --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install bash and curl for health checks and entrypoint
RUN apt-get update && apt-get install -y --no-install-recommends bash curl && rm -rf /var/lib/apt/lists/*

# Copy binaries
COPY --from=build /out/api ./api
COPY --from=build /out/worker ./worker
COPY --from=build /out/nexora-migrate ./nexora-migrate
RUN chmod +x ./nexora-migrate

# Copy appsettings for base defaults
COPY src/Nexora.Api/appsettings.json ./api/appsettings.json
COPY src/Nexora.Api/appsettings.json ./appsettings.json

# Copy entrypoint script
COPY scripts/render-entrypoint.sh ./render-entrypoint.sh
RUN chmod +x ./render-entrypoint.sh

# Create local storage directory
RUN mkdir -p /tmp/nexora-storage && chmod 777 /tmp/nexora-storage

EXPOSE 10000

ENTRYPOINT ["/app/render-entrypoint.sh"]
