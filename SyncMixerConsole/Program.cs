namespace SyncMixerConsole;

using SyncMixerLogic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

internal class Program
{
    static async Task Main(string[] args)
    {
        string appsettingsPath = string.Empty;
        // Create a generic host builder
        var builder = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // Add appsettings.json
                appsettingsPath = hostingContext.HostingEnvironment.ContentRootPath;
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            });

        var host = builder.Build();

        // Access a setting from appsettings.json
        var configuration = host.Services.GetService(typeof(IConfiguration)) as IConfiguration;
        var clientId = configuration?["AppSettings:ClientId"];

        if(string.IsNullOrEmpty(clientId) || clientId == "<YOUR_SPOTIFY_CLIENT_ID>")
        {
            Console.WriteLine("Spotify Client ID is not set in appsettings.json, Please enter ur client id before continuing\n" +
                $"The appsettings.json file is located at: {Path.Combine(appsettingsPath, "appsettings.json")}");
            Console.ReadKey();
            return;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine("Starting SyncManager");
        var syncManager = new SyncManager(); // Renamed variable to avoid conflict
        _ = syncManager.Init(clientId, cts.Token);

        var ok = await syncManager.WhenFinished;

        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(1000, cts.Token);
            if (ok)
            {
                break;
            }
        }
    }
}
