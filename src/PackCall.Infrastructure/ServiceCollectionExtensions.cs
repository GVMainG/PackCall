using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Repositorys;
using PackCall.Infrastructure.Repositories;

namespace PackCall.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services,
            string connectionString)
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(
                    connectionString,
                    pgOptions => pgOptions
                        .MigrationsHistoryTable("__ef_migrations_history")));

            services.AddScoped<ICampaingRepository, CampaignRepository>();
            services.AddScoped<IRecipientRepository, RecipientRepository>();
            services.AddScoped<IDeliveryRepository, DeliveryRepository>();

            return services;
        }
    }
}
