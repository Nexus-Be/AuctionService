namespace Auction.Application;

using Microsoft.Extensions.DependencyInjection;
using Auction.Application.Services.Auction;
using Auction.Application.Services.Bid;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IAuctionService, AuctionService>();
        services.AddScoped<IBidService, BidService>();
        return services;
    }
}
