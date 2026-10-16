using System.Text.Json.Serialization;
using PackCall.Api.Background;
using PackCall.Api.Middleware;
using PackCall.Core.Services;
using PackCall.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.");
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<IDeliveryProcessingService, DeliveryProcessingService>();
builder.Services.AddOptions<DeliveryWorkerOptions>()
    .Bind(builder.Configuration.GetSection(DeliveryWorkerOptions.SectionName))
    .Validate(options => options.IsValid(), "Некорректные параметры DeliveryWorker.")
    .ValidateOnStart();
builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
