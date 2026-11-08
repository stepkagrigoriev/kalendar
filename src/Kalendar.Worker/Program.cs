using System.Diagnostics;
using Kalendar.Worker;
using MongoDB.Driver;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(configuration => configuration
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", "Kalendar.Worker")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMongoDatabase(builder.Configuration);

var app = builder.Build();

await MongoInfrastructure.InitializeAsync(app.Services.GetRequiredService<IMongoDatabase>(), app.Logger);

app.Use(async (httpContext, next) =>
{
    using var correlationId = LogContext.PushProperty("correlationId", httpContext.TraceIdentifier);
    using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
    await next(httpContext);
});

app.UseSerilogRequestLogging();

app.MapGet("/health", () => TypedResults.Ok(new { status = "Healthy" }));
app.MapGet("/ready", MongoInfrastructure.CheckReadinessAsync);

app.Run();
