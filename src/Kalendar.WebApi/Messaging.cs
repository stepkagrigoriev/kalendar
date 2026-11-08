using Kalendar.Contracts;
using MassTransit;

namespace Kalendar.WebApi;

static class Messaging
{
    public static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var host = configuration["RabbitMq:Host"];
        var username = configuration["RabbitMq:Username"];
        var password = configuration["RabbitMq:Password"];
        var port = configuration.GetValue<int?>("RabbitMq:Port") ?? 5672;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || port is < 1 or > 65535)
            throw new InvalidOperationException("Не заданы корректные настройки RabbitMq.");
        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(20);
            options.StopTimeout = TimeSpan.FromSeconds(10);
        });
        services.AddMassTransit(registration =>
        {
            registration.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter("webapi", false));

            registration.UsingRabbitMq((context, bus) =>
            {
                bus.Host(host, (ushort)port, "/", options =>
                {
                    options.Username(username);
                    options.Password(password);
                });
                bus.Message<TaskChanged>(message => message.SetEntityName("task-changed"));
                bus.Message<TaskDeleted>(message => message.SetEntityName("task-deleted"));
                bus.Message<NotificationRequested>(message => message.SetEntityName("notification-requested"));
                bus.Message<BackgroundJobFailed>(message => message.SetEntityName("worker-background-job-failed"));
                bus.UseMessageRetry(retry =>
                {
                    retry.Ignore<ArgumentException>();
                    retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                });
                bus.ConfigureEndpoints(context);
            });
        });
    }
}
