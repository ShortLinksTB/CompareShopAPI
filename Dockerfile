FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["CompareShopAPI.Api/CompareShopAPI.Api.csproj", "CompareShopAPI.Api/"]
COPY ["CompareShopAPI.Application/CompareShopAPI.Application.csproj", "CompareShopAPI.Application/"]
COPY ["CompareShopAPI.Domain/CompareShopAPI.Domain.csproj", "CompareShopAPI.Domain/"]
COPY ["CompareShopAPI.Infrastructure/CompareShopAPI.Infrastructure.csproj", "CompareShopAPI.Infrastructure/"]
COPY ["CompareShopAPI.Workers/CompareShopAPI.Workers.csproj", "CompareShopAPI.Workers/"]
RUN dotnet restore "CompareShopAPI.Api/CompareShopAPI.Api.csproj"

COPY . .
RUN dotnet publish "CompareShopAPI.Api/CompareShopAPI.Api.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CompareShopAPI.Api.dll"]
