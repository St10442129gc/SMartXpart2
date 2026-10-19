FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SmartX.Client/SmartX.Client.csproj", "SmartX.Client/"]
RUN dotnet restore "SmartX.Client/SmartX.Client.csproj"
COPY . .
WORKDIR /src/SmartX.Client
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5081
ENV ASPNETCORE_URLS=http://0.0.0.0:5081
ENV SmartXApi__BaseAddress=http://api:5080
ENTRYPOINT ["dotnet", "SmartX.Client.dll"]
