using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using PackCall.Api.Controllers;
using PackCall.Api.Middleware;
using PackCall.Api.Models;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;

try
{
    var builder = WebApplication.CreateBuilder(args);

    PackCallHosting.LoadConfiguration(builder.Configuration, builder.Environment.EnvironmentName, args);
    var operatorSettings = TestOperatorSettings.Parse(builder.Configuration);
    builder.Services.AddPackCallLogging(builder.Configuration, "PackCall.Api");

    builder.Services.AddControllers().AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddOpenApi(options =>
    {
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(CreateCampaignRequest) &&
                schema.Properties?["channels"] is OpenApiSchema { Items: OpenApiSchema channel })
            {
                channel.Enum = ContactChannel.SupportedChannels.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
            }
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            if (context.Description.ActionDescriptor is ControllerActionDescriptor action &&
                action.ControllerTypeInfo.AsType() == typeof(CampaignsController) &&
                action.MethodInfo.Name == nameof(CampaignsController.CreateCampaign) &&
                operation.Responses?["201"] is OpenApiResponse created)
            {
                created.Headers ??= new Dictionary<string, IOpenApiHeader>();
                created.Headers["Location"] = new OpenApiHeader
                {
                    Required = true,
                    Description = "URL деталей созданной кампании.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }
                };
            }
            return Task.CompletedTask;
        });
    });
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();

    builder.Services.AddStorage(PackCallHosting.ConnectionString(builder.Configuration));
    builder.Services.AddCampaignManagement();
    builder.Services.AddChannelOperators(operatorSettings);
    builder.Services.AddSchemaGate();
    builder.Services.AddPerformanceMeasurement(builder.Configuration, "api");

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
