FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS restore
WORKDIR /src
COPY src/Kalendar.NotificationService/Kalendar.NotificationService.csproj src/Kalendar.NotificationService/
COPY src/Kalendar.Contracts/Kalendar.Contracts.csproj src/Kalendar.Contracts/
RUN dotnet restore src/Kalendar.NotificationService/Kalendar.NotificationService.csproj

FROM restore AS build
COPY src/Kalendar.NotificationService/ src/Kalendar.NotificationService/
COPY src/Kalendar.Contracts/ src/Kalendar.Contracts/
RUN dotnet build src/Kalendar.NotificationService/Kalendar.NotificationService.csproj --no-restore -c Release

FROM build AS publish
RUN dotnet publish src/Kalendar.NotificationService/Kalendar.NotificationService.csproj --no-build -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:80
COPY --from=publish /app/publish .
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=6 CMD ["dotnet", "Kalendar.NotificationService.dll", "--health"]

ENTRYPOINT ["dotnet", "Kalendar.NotificationService.dll"]
