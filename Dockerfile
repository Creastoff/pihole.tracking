# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.Config PiHoleTracking.csproj ./
RUN dotnet restore PiHoleTracking.csproj

COPY . .
RUN dotnet publish PiHoleTracking.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:5178 \
    PIHOLE_REVIEW_DATA_DIR=/data

EXPOSE 5178
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "PiHoleTracking.dll"]
