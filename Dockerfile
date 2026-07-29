# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY global.json ./
COPY src/GhPrApi/GhPrApi.csproj src/GhPrApi/
RUN dotnet restore src/GhPrApi/GhPrApi.csproj

COPY src/GhPrApi src/GhPrApi
RUN dotnet publish src/GhPrApi/GhPrApi.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "GhPrApi.dll"]
