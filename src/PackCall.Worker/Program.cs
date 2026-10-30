using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using PackCall.Worker.Background;

try
{
    var builder = Host.CreateApplicationBuilder(args);
    PackCallHosting.LoadConfiguration(builder.Configuration, builder.Environment.EnvironmentName, args);
    var operatorSettings = TestOperatorSettings.Parse(builder.Configuration);
    var hostName = Environment.MachineName;
    var workerId = $"{hostName[..Math.Min(hostName.Length, 200)]}-{Guid.NewGuid():N}";
    builder.Services.AddPackCallLogging(builder.Configuration, "PackCall.Worker", workerId);
    builder.Services.AddSingleton(new WorkerIdentity(workerId));
    builder.Services.AddStorage(PackCallHosting.ConnectionString(builder.Configuration));
    builder.Services.AddDeliveryProcessing(operatorSettings);
    builder.Services.AddSchemaGate();
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
    builder.Services.AddOptions<DeliveryWorkerOptions>()
        .Bind(builder.Configuration.GetSection(DeliveryWorkerOptions.SectionName))
        .Validate(options => options.IsValid(), "Некорректные параметры DeliveryWorker.").ValidateOnStart();
    builder.Services.AddHostedService<DeliveryWorker>();
    await builder.Build().RunAsync();
    return 0;
}
catch (PackCallConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"PackCall.Worker startup/run failed ({exception.GetType().Name}).");
    return 1;
}
