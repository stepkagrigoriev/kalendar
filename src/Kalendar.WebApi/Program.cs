using System.Diagnostics;
using Kalendar.WebApi;
using Kalendar.WebApi.Auth;
using Kalendar.WebApi.Tasks;
using Microsoft.OpenApi;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;

if (args is ["--health"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try { Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:80/health")).IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
ConventionRegistry.Register("camelCase", new ConventionPack { new CamelCaseElementNameConvention() }, type => type.Namespace?.StartsWith("Kalendar.") == true);

builder.Services.AddSerilog(configuration => configuration
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("serviceName", "Kalendar.WebApi")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMongoDatabase(builder.Configuration);
builder.Services.AddUserAuthentication(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);
builder.Services.AddTelemetry(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Введите JWT-токен без префикса Bearer."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});
builder.Services.AddScoped<TaskEvents>();
builder.Services.AddInternalApi(builder.Configuration);
builder.Services.AddHostedService<TaskMetrics>();

var app = builder.Build();
app.UseTelemetry();
app.UseExceptionHandler();
app.UseStatusCodePages();

await MongoInfrastructure.InitializeAsync(app.Services.GetRequiredService<IMongoDatabase>(), app.Logger);

app.Use(async (httpContext, next) =>
{
    using var correlationId = LogContext.PushProperty("correlationId", httpContext.TraceIdentifier);
    using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
    using var spanId = LogContext.PushProperty("spanId", Activity.Current?.SpanId.ToString());
    await next(httpContext);
});

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => TypedResults.Ok());
app.MapAuthEndpoints();
app.MapTaskEndpoints();
app.MapInternalEndpoints();

app.Run();
