# The Demo Shop with NinjaQueue built from this repository's source (project references, not nuget.org).
# Build context: the repository root (docker compose sets it).

# 1. The dashboard UI (React), built into src/NinjaQueue.Dashboard/wwwroot and embedded in the dashboard assembly.
FROM node:22-alpine AS ui
WORKDIR /repo/src/NinjaQueue.Dashboard/ui
COPY src/NinjaQueue.Dashboard/ui/package.json src/NinjaQueue.Dashboard/ui/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/NinjaQueue.Dashboard/ui/ ./
RUN npm run build

# 2. The app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
COPY --from=ui /repo/src/NinjaQueue.Dashboard/wwwroot src/NinjaQueue.Dashboard/wwwroot
RUN dotnet publish samples/NinjaQueue.Demo/NinjaQueue.Demo.csproj -c Release -o /app -p:NuGetAudit=false

# 3. Runtime: non-root, HTTP on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "NinjaQueue.Demo.dll"]