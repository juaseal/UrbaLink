FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/UrbaLink.Contracts/UrbaLink.Contracts.csproj src/UrbaLink.Contracts/
COPY src/UrbaLink.Api/UrbaLink.Api.csproj src/UrbaLink.Api/
RUN dotnet restore src/UrbaLink.Api/UrbaLink.Api.csproj
COPY src/UrbaLink.Contracts/ src/UrbaLink.Contracts/
COPY src/UrbaLink.Api/ src/UrbaLink.Api/
RUN dotnet publish src/UrbaLink.Api/UrbaLink.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
CMD ["dotnet", "UrbaLink.Api.dll"]
