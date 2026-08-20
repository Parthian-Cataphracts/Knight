using Knight.Contracts.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Knight.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapPlatformHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponseAsync
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteResponseAsync
        });
    }

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        var response = new HealthCheckResponse
        {
            Status = report.Status.ToString(),
            Checks = report.Entries.Select(entry => new HealthCheckEntry
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                DurationMilliseconds = entry.Value.Duration.TotalMilliseconds
            }).ToArray()
        };

        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(response);
    }
}
