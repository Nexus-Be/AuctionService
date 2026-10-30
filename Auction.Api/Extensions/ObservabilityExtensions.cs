namespace Auction.Api.Extensions;

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddServiceObservability(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? configuration["Otel:Endpoint"] ?? "http://localhost:4317";

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation()
                 .AddHttpClientInstrumentation();

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }
}
