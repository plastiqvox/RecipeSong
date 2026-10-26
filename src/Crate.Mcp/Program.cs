using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Crate.Core;
using Crate.Core.Library;
using Crate.Core.Providers;

var builder = Host.CreateApplicationBuilder(args);

// stdio가 MCP 프로토콜 채널이므로 로그는 모두 stderr로
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var settings = AppSettings.Load();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddHttpClient<DeezerClient>();
builder.Services.AddHttpClient<RegionResolver>();
builder.Services.AddHttpClient<ITunesSearchClient>()
    .AddTypedClient((http, _) => new ITunesSearchClient(http, settings.Country));
builder.Services.AddSingleton(sp => new ReleaseRecommender(
    sp.GetRequiredService<DeezerClient>(), sp.GetRequiredService<RegionResolver>()));
builder.Services.AddSingleton<LibraryReleases>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
