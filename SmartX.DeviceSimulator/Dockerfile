FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj", "SmartX.DeviceSimulator/"]
RUN dotnet restore "SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj"
COPY . .
WORKDIR /src/SmartX.DeviceSimulator
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV SMARTX_API=http://api:5080
ENTRYPOINT ["dotnet", "SmartX.DeviceSimulator.dll"]
