  # syntax=docker/dockerfile:1

  FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
  WORKDIR /src

  COPY BybitGridBot.sln .
  COPY src ./src
  COPY tests ./tests

  RUN dotnet restore BybitGridBot.sln
  RUN dotnet publish src/BybitGridBot.App/BybitGridBot.App.csproj -c Release -o /app/publish /p:UseAppHost=false

  FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
  WORKDIR /app

  ENV DOTNET_EnableDiagnostics=0

  COPY --from=build /app/publish .

  VOLUME ["/app/data", "/app/logs"]

  ENTRYPOINT ["dotnet", "BybitGridBot.App.dll"]
