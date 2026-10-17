using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CompareShopAPI.Api.Observability;

public static class TelemetryRegistration
{
    public const string DefaultServiceName = "compareshop-api";

    public static OpenTelemetryBuilder AddCompareShopTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var serviceName = configuration["OTEL_SERVICE_NAME"]
            ?? configuration["OpenTelemetry:ServiceName"]
            ?? DefaultServiceName;
        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? configuration["OpenTelemetry:OtlpEndpoint"];

        return services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation()
                    .AddRedisInstrumentation();

                tracing.AddOtlpExporter(options =>
                {
                    if (!string.IsNullOrWhiteSpace(endpoint))
                        options.Endpoint = new Uri(endpoint);
                });
            });
    }
}
