namespace Auction.Application;

using Microsoft.Extensions.DependencyInjection;
using Auction.Application.Services.Auction;
using Auction.Application.Services.AuctionConfig;
using Auction.Application.Services.AuctionLifecycle;
using Auction.Application.Services.Bid;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAuctionService, AuctionService>();
        services.AddScoped<IBidService, BidService>();
        services.AddScoped<IAuctionConfigService, AuctionConfigService>();
        services.AddScoped<IAuctionLifecycleService, AuctionLifecycleService>();
        return services;
    }
}