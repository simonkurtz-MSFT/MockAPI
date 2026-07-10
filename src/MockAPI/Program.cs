using MockAPI.Configuration;
using MockAPI.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});
builder.Services.AddSingleton<ConfigurationState>();
builder.Services.AddSingleton<RequestStatisticsCollector>();

var app = builder.Build();
var configuration = app.Services.GetRequiredService<ConfigurationState>();
var statistics = app.Services.GetRequiredService<RequestStatisticsCollector>();

app.Run(context => MockRequestDispatcher.DispatchAsync(context, configuration, statistics));

app.Run();

public partial class Program;
