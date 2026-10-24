FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/API/Trykatch.Api/Trykatch.Api.csproj --locked-mode \
    && dotnet publish src/API/Trykatch.Api/Trykatch.Api.csproj -c Release -o /app --no-restore /p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca AS final
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl=8.5.0-2ubuntu10.16 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 CMD ["curl", "--fail", "--silent", "http://localhost:8080/health/live"]
ENTRYPOINT ["dotnet", "Trykatch.Api.dll"]
