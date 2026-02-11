using HelixTool.Core;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IHelixApiClient>(_ => new HelixApiClient(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
builder.Services.AddSingleton<HelixService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "hlx", Version = "1.0.0" };
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly);

var app = builder.Build();
app.MapMcp();
app.Run();
