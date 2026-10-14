FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY products/civicops/CivicOps.csproj products/civicops/
RUN dotnet restore products/civicops/CivicOps.csproj
COPY products/civicops/ products/civicops/
COPY docs/schemas/ docs/schemas/
RUN dotnet publish products/civicops/CivicOps.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false
COPY deploy/container/HealthProbe/ deploy/container/HealthProbe/
RUN dotnet publish deploy/container/HealthProbe/HealthProbe.csproj --configuration Release --output /app/healthcheck /p:UseAppHost=false \
    && mkdir -p /app/data

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /app/publish/ ./
COPY --from=build --chown=1654:1654 /app/healthcheck/ /healthcheck/
COPY --from=build --chown=1654:1654 /app/data/ /app/Data/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    CIVICOPS_DATA_DIR=/app/Data \
    CIVICOPS_DEMO_MODE=false \
    CIVICOPS_EMAIL_MODE=outbox \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8080
VOLUME ["/app/Data"]
HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 CMD ["dotnet", "/healthcheck/HealthProbe.dll"]
ENTRYPOINT ["dotnet", "CivicOps.dll"]
