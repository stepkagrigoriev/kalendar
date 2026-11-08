using System.Diagnostics;
using Kalendar.WebApi;
using Kalendar.WebApi.Auth;
using Kalendar.WebApi.Tasks;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
ConventionRegistry.Register("camelCase", new ConventionPack { new CamelCaseElementNameConvention() }, type => type.Namespace?.StartsWith("Kalendar.") == true);

builder.Services.AddSerilog(configuration => configuration
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", "Kalendar.WebApi")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMongoDatabase(builder.Configuration);
builder.Services.AddUserAuthentication(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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
app.MapAuthEndpoints();
app.MapTaskEndpoints();

app.Run();
