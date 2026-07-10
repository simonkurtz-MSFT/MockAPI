var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
	options.AddServerHeader = false;
});

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
