FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8005
ENV ASPNETCORE_URLS=http://+:8005

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Auction.sln", "."]
COPY ["Auction.Api/Auction.Api.csproj", "Auction.Api/"]
COPY ["Auction.Application/Auction.Application.csproj", "Auction.Application/"]
COPY ["Auction.Domain/Auction.Domain.csproj", "Auction.Domain/"]
COPY ["Auction.Infrastructure/Auction.Infrastructure.csproj", "Auction.Infrastructure/"]
RUN dotnet restore "Auction.sln"
COPY . .
RUN dotnet build "Auction.Api/Auction.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Auction.Api/Auction.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Auction.Api.dll"]
