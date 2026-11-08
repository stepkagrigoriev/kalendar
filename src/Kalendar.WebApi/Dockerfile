FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS restore
WORKDIR /src
COPY src/Kalendar.WebApi/Kalendar.WebApi.csproj src/Kalendar.WebApi/
COPY src/Kalendar.Contracts/Kalendar.Contracts.csproj src/Kalendar.Contracts/
RUN dotnet restore src/Kalendar.WebApi/Kalendar.WebApi.csproj

FROM restore AS build
COPY src/Kalendar.WebApi/ src/Kalendar.WebApi/
COPY src/Kalendar.Contracts/ src/Kalendar.Contracts/
RUN dotnet build src/Kalendar.WebApi/Kalendar.WebApi.csproj --no-restore -c Release

FROM build AS publish
RUN dotnet publish src/Kalendar.WebApi/Kalendar.WebApi.csproj --no-build -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:80
COPY --from=publish /app/publish .
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=6 CMD ["dotnet", "Kalendar.WebApi.dll", "--health"]

ENTRYPOINT ["dotnet", "Kalendar.WebApi.dll"]
