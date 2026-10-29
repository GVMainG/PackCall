using System.Text.Json.Serialization;
using PackCall.Api.Middleware;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;

try
{
    var builder = WebApplication.CreateBuilder(args);

    PackCallHosting.LoadConfiguration(builder.Configuration, builder.Environment.EnvironmentName, args);
    var operatorSettings = TestOperatorSettings.Parse(builder.Configuration);
    builder.Services.AddPackCallLogging(builder.Configuration, "PackCall.Api");

    RegisterServices(builder);

    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "PackCall API v1"));
    }

    app.MapControllers();

    app.Run();
    return 0;
}
catch (PackCallConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"PackCall.Api startup/run failed ({exception.GetType().Name}).");
    return 1;
}

# region Методы

static void RegisterServices(WebApplicationBuilder builder)
{
    builder.Services.AddControllers().AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddOpenApi();
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();

    builder.Services.AddStorage(PackCallHosting.ConnectionString(builder.Configuration));
    builder.Services.AddCampaignManagement();
    builder.Services.AddSchemaGate();
    builder.Services.AddPerformanceMeasurement(builder.Configuration, "api");
}

#endregion Методы
