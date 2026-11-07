# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/LedgerFlow/LedgerFlow.csproj src/LedgerFlow/packages.lock.json src/LedgerFlow/
# Optional corporate/cloud CA trust for the restore step; never copied into an image layer.
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then SSL_CERT_FILE=/run/secrets/proxy_ca dotnet restore src/LedgerFlow --locked-mode; else dotnet restore src/LedgerFlow --locked-mode; fi
COPY src/LedgerFlow/ src/LedgerFlow/
RUN dotnet publish src/LedgerFlow -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "LedgerFlow.dll"]
