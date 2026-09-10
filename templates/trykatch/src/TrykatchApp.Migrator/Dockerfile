FROM mcr.microsoft.com/dotnet/sdk:10.0.301@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj --locked-mode \
    && dotnet publish src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:011bb5f30180717b1c8b65822ff2c99bcb96bc65af0164589751b83c7b4949f7 AS final
RUN apt-get update \
    && apt-get install --no-install-recommends -y libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "TrykatchApp.Migrator.dll"]
