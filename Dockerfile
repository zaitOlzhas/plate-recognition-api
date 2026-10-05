# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/LprWebhook.Api/LprWebhook.Api.csproj src/LprWebhook.Api/
RUN dotnet restore src/LprWebhook.Api/LprWebhook.Api.csproj
COPY src/ src/
RUN dotnet publish src/LprWebhook.Api/LprWebhook.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- tests (only built on demand: docker build --target test .) ----
FROM build AS test
COPY tests/LprWebhook.Api.Tests/LprWebhook.Api.Tests.csproj tests/LprWebhook.Api.Tests/
RUN dotnet restore tests/LprWebhook.Api.Tests/LprWebhook.Api.Tests.csproj
COPY tests/ tests/
RUN dotnet test tests/LprWebhook.Api.Tests/LprWebhook.Api.Tests.csproj -c Release --no-restore --logger "console;verbosity=normal"

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# /data holds the SQLite file; owned by the image's built-in non-root "app" user.
RUN mkdir -p /data && chown "$APP_UID" /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/data/events.db"
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
VOLUME /data
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "LprWebhook.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "LprWebhook.Api.dll"]
