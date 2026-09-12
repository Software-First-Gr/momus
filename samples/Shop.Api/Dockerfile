# Build from the repository root:  docker build -f samples/Shop.Api/Dockerfile .
#
# The demo shop is the one sample that references Momus.Client from source rather than from
# nuget.org, so the build needs those two projects as well as its own.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first, against the project files alone, so editing code does not re-resolve packages.
COPY Directory.Build.props README.md ./
COPY src/Momus.Core/Momus.Core.csproj src/Momus.Core/
COPY src/Momus.Client/Momus.Client.csproj src/Momus.Client/
COPY samples/Shop.Api/Shop.Api.csproj samples/Shop.Api/
RUN dotnet restore samples/Shop.Api/Shop.Api.csproj

COPY src/Momus.Core/ src/Momus.Core/
COPY src/Momus.Client/ src/Momus.Client/
COPY samples/Shop.Api/ samples/Shop.Api/

# What makes one build of the shop a different deploy from another. See docker-compose.yml.
ARG SHOP_VERSION=1.0.0
ARG SHOP_SLOW_BUILD=false
RUN dotnet publish samples/Shop.Api/Shop.Api.csproj -c Release -o /app --no-restore \
    -p:ShopVersion=$SHOP_VERSION -p:ShopSlowBuild=$SHOP_SLOW_BUILD

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
