FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["custom-metrics-emitter.csproj", "."]
RUN dotnet restore "custom-metrics-emitter.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "custom-metrics-emitter.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "custom-metrics-emitter.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "custom-metrics-emitter.dll"]
