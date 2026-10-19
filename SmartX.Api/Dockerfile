FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SmartX.Api/SmartX.Api.csproj", "SmartX.Api/"]
RUN dotnet restore "SmartX.Api/SmartX.Api.csproj"
COPY . .
WORKDIR /src/SmartX.Api
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5080
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
