using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(configuration => configuration
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", "Kalendar.Worker")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

var app = builder.Build();

app.Use(async (httpContext, next) =>
{
    using var correlationId = LogContext.PushProperty("correlationId", httpContext.TraceIdentifier);
    using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
    await next(httpContext);
});

app.UseSerilogRequestLogging();

app.MapGet("/health", () => TypedResults.Ok(new { status = "Healthy" }));
app.MapGet("/ready", () => TypedResults.Ok(new { status = "Healthy" }));

app.Run();
