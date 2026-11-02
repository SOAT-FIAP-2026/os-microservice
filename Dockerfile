# syntax=docker/dockerfile:1

# ---------- Stage 1: Build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/OrdemServico.Lambda/OrdemServico.Lambda.csproj src/OrdemServico.Lambda/
RUN dotnet restore src/OrdemServico.Lambda/OrdemServico.Lambda.csproj

COPY src/ src/
RUN dotnet publish src/OrdemServico.Lambda/OrdemServico.Lambda.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- Stage 2: Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build --chown=app:app /app/publish .
USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "OrdemServico.Lambda.dll"]

