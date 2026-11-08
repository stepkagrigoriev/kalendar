FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY tools/Kalendar.DatabaseSetup/ .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0.12
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Kalendar.DatabaseSetup.dll"]
