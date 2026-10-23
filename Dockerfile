FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MandalaCore/MandalaCore.csproj src/MandalaCore/
COPY src/MandalaWeb.Client/MandalaWeb.Client.csproj src/MandalaWeb.Client/
COPY src/MandalaWeb.Server/MandalaWeb.Server.csproj src/MandalaWeb.Server/
RUN dotnet restore src/MandalaWeb.Server/MandalaWeb.Server.csproj
COPY . .
RUN dotnet publish src/MandalaWeb.Server/MandalaWeb.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MandalaWeb.Server.dll"]
