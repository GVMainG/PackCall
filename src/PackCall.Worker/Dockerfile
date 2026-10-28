FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /src
COPY ["src/PackCall.Worker/PackCall.Worker.csproj", "src/PackCall.Worker/"]
COPY ["src/PackCall.Core/PackCall.Core.csproj", "src/PackCall.Core/"]
COPY ["src/PackCall.Infrastructure/PackCall.Infrastructure.csproj", "src/PackCall.Infrastructure/"]
RUN dotnet restore src/PackCall.Worker/PackCall.Worker.csproj
COPY . .
RUN dotnet publish src/PackCall.Worker/PackCall.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PackCall.Worker.dll"]
