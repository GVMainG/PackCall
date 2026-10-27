using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Journaling;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Services;
using PackCall.Infrastructure.Journaling;
using PackCall.Infrastructure.Repositories;
using PackCall.Infrastructure.Sending;

namespace PackCall.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services,
            string connectionString, Hosting.TestOperatorSettings settings) => services
            .AddStorage(connectionString).AddDeliveryProcessing(settings);

        public static IServiceCollection AddStorage(this IServiceCollection services, string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connectionString,
                postgres => postgres.CommandTimeout(30)));
            return services;
        }

        private static IServiceCollection AddRepositories(this IServiceCollection services) => services
            .AddScoped<ICampaingRepository, CampaignRepository>()
            .AddScoped<IRecipientRepository, RecipientRepository>()
            .AddScoped<IDeliveryRepository, DeliveryRepository>();

        public static IServiceCollection AddCampaignManagement(this IServiceCollection services) => services
            .AddRepositories().AddScoped<ICampaignService, CampaignService>();

        public static IServiceCollection AddDeliveryProcessing(this IServiceCollection services, Hosting.TestOperatorSettings settings)
        {
            services.AddRepositories()
                .AddScoped<IDeliveryProcessingService, DeliveryProcessingService>()
                .AddSingleton<PackCall.Core.ILeaseClock, PostgresLeaseClock>()
                .AddSingleton<IDeliveryStatusJournal, DeliveryStatusJournal>()
                .AddSingleton(new SendPolicy(settings.SimulateErrors));
            if (settings.Mode == Hosting.TestSenderMode.Inprocess)
                services.AddSingleton<IOperatorRandom, OperatorRandom>().AddSingleton<IMessageSender, MockEmailSender>();
            else
                services.AddSingleton<IMessageSender>(_ => new HttpTestEmailSender(settings));
            return services;
        }
    }
}
