# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT_PATH=BybitBot.csproj

WORKDIR /src

COPY . .

RUN dotnet restore "${PROJECT_PATH}"
RUN dotnet publish "${PROJECT_PATH}" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
ARG APP_DLL=BybitBot.dll

WORKDIR /app

ENV APP_DLL=${APP_DLL}
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

ENTRYPOINT ["sh", "-c", "dotnet \"$APP_DLL\""]
