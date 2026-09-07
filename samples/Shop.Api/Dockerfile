# Build from the repository root:  docker build -f samples/Shop.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY samples/Shop.Api/Shop.Api.csproj samples/Shop.Api/
RUN dotnet restore samples/Shop.Api/Shop.Api.csproj

COPY samples/Shop.Api/ samples/Shop.Api/
RUN dotnet publish samples/Shop.Api/Shop.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
