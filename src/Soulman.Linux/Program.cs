using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Soulman;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Default config location
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "soulman", "soulman.json");

        // Also check executable directory
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("soulman.json", optional: true, reloadOnChange: true)
            .AddJsonFile(configPath, optional: true, reloadOnChange: true);

        builder.Services.AddHostedService<Worker>();
        builder.Services.AddSingleton<DownloadScanner>();
        builder.Services.AddSingleton<MoveLogStore>();
        builder.Services.AddSingleton<PathPreferenceStore>();
        builder.Services.AddSingleton<MoveNotificationBroker>();
        builder.Services.AddSingleton<InstanceDiscovery>();

        builder.Services.Configure<SoulmanSettings>(builder.Configuration.GetSection("Soulman"));

        builder.Services.AddSystemd();

        var host = builder.Build();
        host.Run();
    }
}
