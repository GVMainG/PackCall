using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Journaling;
using PackCall.Core.Metrics;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Infrastructure.Journaling;
using PackCall.Infrastructure.Metrics;
using PackCall.Infrastructure.Repositories;
using PackCall.Infrastructure.Sending;

namespace PackCall.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services,
            string connectionString,
            Action<MockEmailOptions>? configureMockEmail = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            services.AddDbContextFactory<AppDbContext>(options =>
                options.UseNpgsql(
                    connectionString,
                    pgOptions => pgOptions
                        .MigrationsHistoryTable("__ef_migrations_history")));

            if (configureMockEmail is null)
                services.AddOptions<MockEmailOptions>();
            else
                services.Configure(configureMockEmail);

            services.AddScoped<ICampaingRepository, CampaignRepository>();
            services.AddScoped<IRecipientRepository, RecipientRepository>();
            services.AddScoped<IDeliveryRepository, DeliveryRepository>();
            services.AddSingleton<IDeliveryStatusJournal, DeliveryStatusJournal>();
            // Сбор и публикация метрик временно отключены.
            // services.AddSingleton<DeliveryMetrics>();
            // services.AddSingleton<IDeliveryMetrics>(provider => provider.GetRequiredService<DeliveryMetrics>());
            // services.AddScoped<IDeliveryStateSnapshotReader, DeliveryStateSnapshotReader>();
            services.AddSingleton<IDeliveryMetrics, NoOpDeliveryMetrics>();
            services.AddSingleton<IMessageSender, MockEmailSender>();

            return services;
        }
    }
}
