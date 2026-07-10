using MockAPI.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});
builder.Services.AddSingleton<EndpointRegistry>();
builder.Services.AddSingleton<RequestStatisticsCollector>();

var app = builder.Build();
var registry = app.Services.GetRequiredService<EndpointRegistry>();
var statistics = app.Services.GetRequiredService<RequestStatisticsCollector>();

app.Run(context => MockRequestDispatcher.DispatchAsync(context, registry, statistics));

app.Run();

public partial class Program;
