FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/FlatpackApp.Api/FlatpackApp.Api.csproj --locked-mode \
    && dotnet publish src/FlatpackApp.Api/FlatpackApp.Api.csproj -c Release -o /app --no-restore /p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-bookworm-slim AS final
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl=7.88.1-10+deb12u14 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 CMD ["curl", "--fail", "--silent", "http://localhost:8080/health/live"]
ENTRYPOINT ["dotnet", "FlatpackApp.Api.dll"]
