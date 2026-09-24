# Build from the repository root:  docker build -f src/Momus.Cli/Dockerfile -t momus .
#
# One image, both commands:
#   docker run -d -p 4848:4848 -v momus-data:/data momus              # serve
#   docker run --rm momus scan -p postgres -c "Host=…;Database=…"     # one-off scan

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first, against the project files alone, so editing code does not re-resolve packages.
COPY Directory.Build.props README.md ./
COPY src/Momus.Core/Momus.Core.csproj src/Momus.Core/
COPY src/Momus.Postgres/Momus.Postgres.csproj src/Momus.Postgres/
COPY src/Momus.SqlServer/Momus.SqlServer.csproj src/Momus.SqlServer/
COPY src/Momus.Server/Momus.Server.csproj src/Momus.Server/
COPY src/Momus.Cli/Momus.Cli.csproj src/Momus.Cli/
RUN dotnet restore src/Momus.Cli/Momus.Cli.csproj

COPY src/ src/
# CI passes the git tag (v0.1.0); a local build keeps whatever Directory.Build.props says.
ARG VERSION=""
RUN dotnet publish src/Momus.Cli/Momus.Cli.csproj -c Release -o /app --no-restore \
    $( [ -n "$VERSION" ] && echo -p:Version=${VERSION#v} )

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# The Alpine image runs .NET in globalization-invariant mode, and Microsoft.Data.SqlClient refuses
# to open a connection in it ("Globalization Invariant Mode is not supported"), so every SQL Server
# scan failed before a single check ran. Npgsql does not care, which is why the Postgres demo never
# showed it. icu-data-full rather than the English-only data: a server's collation can be any
# language, and SqlClient resolves it through ICU.
RUN apk add --no-cache icu-libs icu-data-full
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app
COPY --from=build /app ./

# The SQLite file lives on the volume; everything else in the container is disposable.
ENV MOMUS_DATA=/data \
    MOMUS_PORT=4848 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_HTTP_PORTS="" 
VOLUME /data
EXPOSE 4848

# Entrypoint and command are split so `docker run momus scan …` works as well as the default serve.
ENTRYPOINT ["dotnet", "Momus.Cli.dll"]
CMD ["serve"]
