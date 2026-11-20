# Vértice en modo servidor: interfaz React + API ASP.NET Core con SignalR (el motor corre en el servidor)
FROM node:24-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
ENV VITE_ENGINE=server
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY Vertice.sln ./
COPY src/Vertice.Core/Vertice.Core.csproj src/Vertice.Core/
COPY src/Vertice.Api/Vertice.Api.csproj src/Vertice.Api/
RUN dotnet restore src/Vertice.Api/Vertice.Api.csproj
COPY src/Vertice.Core/ src/Vertice.Core/
COPY src/Vertice.Api/ src/Vertice.Api/
RUN dotnet publish src/Vertice.Api/Vertice.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Vertice.Api.dll"]
