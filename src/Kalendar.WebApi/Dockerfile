FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Kalendar.WebApi/ .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:80
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Kalendar.WebApi.dll"]
