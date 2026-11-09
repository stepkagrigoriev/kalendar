using System.Diagnostics;
using Kalendar.Worker;
using Kalendar.Worker.Scheduling;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;

if (args is ["--health"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try { Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:80/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
ConventionRegistry.Register("camelCase", new ConventionPack { new CamelCaseElementNameConvention() }, type => type.Namespace?.StartsWith("Kalendar.") == true);

builder.Services.AddSerilog(configuration => configuration
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("serviceName", "Kalendar.Worker")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMongoDatabase(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);
builder.Services.AddTelemetry(builder.Configuration);
builder.Services.AddScheduler(builder.Configuration);
builder.Services.AddHostedService<JobMetrics>();

var app = builder.Build();
app.UseTelemetry();

await MongoInfrastructure.InitializeAsync(app.Services.GetRequiredService<IMongoDatabase>(), app.Logger);

app.Use(async (httpContext, next) =>
{
    using var correlationId = LogContext.PushProperty("correlationId", httpContext.TraceIdentifier);
    using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
    using var spanId = LogContext.PushProperty("spanId", Activity.Current?.SpanId.ToString());
    await next(httpContext);
});

app.UseSerilogRequestLogging();

app.MapGet("/health", () => TypedResults.Ok(new { status = "Healthy" }));
app.MapGet("/ready", MongoInfrastructure.CheckReadinessAsync);

app.Run();
