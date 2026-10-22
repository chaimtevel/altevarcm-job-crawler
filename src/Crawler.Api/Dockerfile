# Build context is the repository root (see docker-compose.yml).
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Crawler.Domain/Crawler.Domain.csproj src/Crawler.Domain/
COPY src/Crawler.Infrastructure/Crawler.Infrastructure.csproj src/Crawler.Infrastructure/
COPY src/Crawler.Api/Crawler.Api.csproj src/Crawler.Api/
RUN dotnet restore src/Crawler.Api/Crawler.Api.csproj

COPY src/ src/
RUN dotnet publish src/Crawler.Api/Crawler.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# The API serves the React UI, so UI and API share one origin (no CORS).
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Crawler.Api.dll"]
