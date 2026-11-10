# Hosts.Api host image. Build context is the repository root (see deploy/compose/docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /repo
COPY global.json ./
COPY src ./src
RUN dotnet publish src/Hosts.Api/Hosts.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Hosts.Api.dll"]
