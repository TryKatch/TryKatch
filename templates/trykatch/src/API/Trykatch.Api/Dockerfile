FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:2794448a07118a6eaf00f29ab5d2bf44928517ce5ff86505a63a69230374f599 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/API/Trykatch.Api/Trykatch.Api.csproj --locked-mode \
    && dotnet publish src/API/Trykatch.Api/Trykatch.Api.csproj -c Release -o /app --no-restore /p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0@sha256:ca396ece3f2a2e62c7a03f1d7d710b8bc7e8c9dea5597d091178d9f5bfca13f0 AS final
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl=8.18.0-1ubuntu2.8 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 CMD ["curl", "--fail", "--silent", "http://localhost:8080/health/live"]
ENTRYPOINT ["dotnet", "Trykatch.Api.dll"]
