# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ora2PgVerify.Core/Ora2PgVerify.Core.csproj src/Ora2PgVerify.Core/
COPY src/Ora2PgVerify.Oracle/Ora2PgVerify.Oracle.csproj src/Ora2PgVerify.Oracle/
COPY src/Ora2PgVerify.Postgres/Ora2PgVerify.Postgres.csproj src/Ora2PgVerify.Postgres/
COPY src/Ora2PgVerify.Cli/Ora2PgVerify.Cli.csproj src/Ora2PgVerify.Cli/
RUN dotnet restore src/Ora2PgVerify.Cli/Ora2PgVerify.Cli.csproj

COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Ora2PgVerify.Cli/Ora2PgVerify.Cli.csproj -c Release --no-restore -o /app -p:UseAppHost=false
# Report directory owned by the non-root app user; a named volume mounted here inherits the ownership.
RUN mkdir -p /out

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu "extra": no shell or package manager, non-root UID 1654, but with ICU and tzdata
# (culture-aware text handling in the drivers and time zone data).
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS runtime
LABEL org.opencontainers.image.title="ora2pg-verify" \
      org.opencontainers.image.description="Oracle to PostgreSQL migration verification: PL/SQL scan, chunked row comparison, dual-write audit" \
      org.opencontainers.image.source="https://github.com/hekimcan206/ora2pg-verify" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app /app
COPY --from=build --chown=1654:1654 /out /out
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/ora2pg-verify.dll"]
CMD ["--help"]
