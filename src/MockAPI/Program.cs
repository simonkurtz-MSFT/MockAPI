using MockAPI.Configuration;
using MockAPI.Runtime;

var builder = WebApplication.CreateBuilder(args);
var configurationPath = builder.Configuration[$"{MockApiOptions.SectionName}:ConfigurationPath"];
if (string.IsNullOrWhiteSpace(configurationPath))
{
    configurationPath = builder.Environment.IsDevelopment()
        ? Path.Combine(builder.Environment.ContentRootPath, "mockapi.json")
        : "/data/mockapi.json";
}

var allowEmptyValue = builder.Configuration[$"{MockApiOptions.SectionName}:AllowEmptyConfiguration"];
if (!string.IsNullOrWhiteSpace(allowEmptyValue) && !bool.TryParse(allowEmptyValue, out _))
{
    throw new InvalidOperationException("MockApi:AllowEmptyConfiguration must be 'true' or 'false'.");
}

var options = new MockApiOptions
{
    ConfigurationPath = configurationPath,
    AllowEmptyConfiguration = string.IsNullOrWhiteSpace(allowEmptyValue) || bool.Parse(allowEmptyValue)
};

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ConfigurationState>();
builder.Services.AddSingleton<ConfigurationFileStore>();
builder.Services.AddSingleton<RequestStatisticsCollector>();

var app = builder.Build();
var configuration = app.Services.GetRequiredService<ConfigurationState>();
var configurationStore = app.Services.GetRequiredService<ConfigurationFileStore>();
var statistics = app.Services.GetRequiredService<RequestStatisticsCollector>();

await configurationStore.LoadAsync(configuration, CancellationToken.None);

app.Run(context => MockRequestDispatcher.DispatchAsync(context, configuration, statistics));

app.Run();

public partial class Program;
