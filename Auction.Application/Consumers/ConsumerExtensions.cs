namespace Auction.Application.Consumers;

using MassTransit;

public static class ConsumerExtensions
{
    public static IBusRegistrationConfigurator AddApplicationConsumers(this IBusRegistrationConfigurator configurator)
    {
        // Register Auction service consumers here when inbound events are added
        return configurator;
    }
}
