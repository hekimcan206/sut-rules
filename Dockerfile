# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SutRules.Core/SutRules.Core.csproj src/SutRules.Core/
COPY src/SutRules.Api/SutRules.Api.csproj src/SutRules.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/SutRules.Api/SutRules.Api.csproj

COPY .editorconfig ./
COPY src/SutRules.Core/ src/SutRules.Core/
COPY src/SutRules.Api/ src/SutRules.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/SutRules.Api/SutRules.Api.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="sut-rules" \
      org.opencontainers.image.source="https://github.com/hekimcan206/sut-rules" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
# The shipped rules are baked in so the image works on its own; compose mounts ./rules over them for live edits.
COPY --link rules/ /app/rules/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SutRules.Api.dll"]
