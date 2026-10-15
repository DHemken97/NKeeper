# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until project files change.
COPY Directory.Build.props NKeeper.slnx ./
COPY src/NKeeper.Api/NKeeper.Api.csproj src/NKeeper.Api/
COPY src/NKeeper.Infrastructure/NKeeper.Infrastructure.csproj src/NKeeper.Infrastructure/
RUN dotnet restore src/NKeeper.Api/NKeeper.Api.csproj

COPY src/ src/
RUN dotnet publish src/NKeeper.Api/NKeeper.Api.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .

# The official image ships a non-root "app" user; never run the API as root.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "NKeeper.Api.dll"]
