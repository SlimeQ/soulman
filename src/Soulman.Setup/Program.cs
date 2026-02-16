using Spectre.Console;
using System.Text.Json;

namespace SoulmanSetup;

class Program
{
    static void Main(string[] args)
    {
        // 1. Argument Parsing
        string configPath = "soulman.json";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                configPath = args[i + 1];
            }
        }

        // 2. Load Existing Config or Default
        var config = new SoulmanConfig();
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                // Try to load as wrapped { Soulman: ... } first
                try 
                {
                    var root = JsonSerializer.Deserialize<RootConfig>(json);
                    if (root?.Soulman != null) config = root.Soulman;
                    else 
                    {
                        var loaded = JsonSerializer.Deserialize<SoulmanConfig>(json);
                        if (loaded != null) config = loaded;
                    }
                }
                catch
                {
                    var loaded = JsonSerializer.Deserialize<SoulmanConfig>(json);
                    if (loaded != null) config = loaded;
                }
            }
            catch
            {
                AnsiConsole.MarkupLine("[red]Warning: Could not load existing config. Using defaults.[/]");
            }
        }

        // 3. Header / Logo
        AnsiConsole.Clear();
        AnsiConsole.Write(
            new FigletText("SOULMAN")
                .Color(Color.Cyan1));
        
        AnsiConsole.MarkupLine("[bold white]Distributed Media Library Manager Setup[/]");
        AnsiConsole.MarkupLine($"[grey]Config file: {configPath}[/]");
        AnsiConsole.WriteLine();

        // 4. Wizard Steps
        
        // --- Node Identity ---
        AnsiConsole.Write(new Rule("[yellow]Node Identity[/]"));
        
        config.NodeName = AnsiConsole.Ask<string>("Node [green]Name[/]:", config.NodeName);
        config.DiscoveryPort = AnsiConsole.Ask<int>("Discovery [green]Port[/]:", config.DiscoveryPort);

        // --- Node Behavior ---
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Node Behavior[/]"));

        config.DownloadFromSoulseek = AnsiConsole.Confirm("Download from [green]Soulseek[/] directly?", config.DownloadFromSoulseek);
        config.ReceiveFromPeers = AnsiConsole.Confirm("Receive files from [green]peers[/]?", config.ReceiveFromPeers);
        
        AnsiConsole.MarkupLine("[grey]Note: All nodes always give (share to peers).[/]");

        // --- Soulseek Config (Conditional) ---
        if (config.DownloadFromSoulseek)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Soulseek Configuration[/]"));

            config.SoulseekUsername = AnsiConsole.Ask<string>("Soulseek [green]Username[/]:", config.SoulseekUsername ?? "");
            config.SoulseekPassword = AnsiConsole.Prompt(
                new TextPrompt<string>("Soulseek [green]Password[/]:")
                    .PromptStyle("red")
                    .Secret());
            
            config.SourcePath = AnsiConsole.Ask<string>("Soulseek [green]Download Folder[/]:", config.SourcePath ?? "Downloads/Soulseek");
        }
        else
        {
            // If not downloading, maybe SourcePath is a watched folder?
            config.SourcePath = AnsiConsole.Ask<string>("Local [green]Source Folder[/] to watch:", config.SourcePath ?? "Downloads/Soulseek");
        }

        // --- Media Gathering Rules ---
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Media Gathering Rules[/]"));

        config.GatherMusic = AnsiConsole.Confirm("Gather [green]Music[/]?", config.GatherMusic);
        config.GatherMovies = AnsiConsole.Confirm("Gather [green]Movies[/]?", config.GatherMovies);
        config.GatherTV = AnsiConsole.Confirm("Gather [green]TV[/]?", config.GatherTV);

        // --- Library Paths ---
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Library Paths[/]"));

        if (config.GatherMusic)
            config.MusicLibraryPath = AnsiConsole.Ask<string>("Music Library [green]Path[/]:", config.MusicLibraryPath ?? "");
        
        if (config.GatherMovies)
            config.MoviesLibraryPath = AnsiConsole.Ask<string>("Movies Library [green]Path[/]:", config.MoviesLibraryPath ?? "");

        if (config.GatherTV)
            config.TvLibraryPath = AnsiConsole.Ask<string>("TV Library [green]Path[/]:", config.TvLibraryPath ?? "");

        // 5. Summary & Confirmation
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("SOULMAN").Color(Color.Cyan1));
        AnsiConsole.Write(new Rule("[yellow]Configuration Summary[/]"));

        var table = new Table();
        table.AddColumn("Setting");
        table.AddColumn("Value");

        table.AddRow("Node Name", config.NodeName);
        table.AddRow("Discovery Port", config.DiscoveryPort.ToString());
        table.AddRow("Download from Soulseek", config.DownloadFromSoulseek ? "[green]Yes[/]" : "[red]No[/]");
        table.AddRow("Receive From Peers", config.ReceiveFromPeers ? "[green]Yes[/]" : "[red]No[/]");

        if (config.DownloadFromSoulseek)
        {
            table.AddRow("Soulseek User", config.SoulseekUsername ?? "-");
            table.AddRow("Soulseek Password", "******");
            table.AddRow("Soulseek DL Folder", config.SourcePath ?? "-");
        }
        else
        {
            table.AddRow("Source Path", config.SourcePath ?? "-");
        }

        table.AddRow("Gather Music", config.GatherMusic ? $"[green]Yes[/] ({config.MusicLibraryPath})" : "[red]No[/]");
        table.AddRow("Gather Movies", config.GatherMovies ? $"[green]Yes[/] ({config.MoviesLibraryPath})" : "[red]No[/]");
        table.AddRow("Gather TV", config.GatherTV ? $"[green]Yes[/] ({config.TvLibraryPath})" : "[red]No[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (AnsiConsole.Confirm($"Save configuration to [blue]{configPath}[/]?", true))
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            // Wrap in "Soulman" property for IConfiguration binding compatibility
            var payload = new { Soulman = config };
            var jsonString = JsonSerializer.Serialize(payload, options);
            File.WriteAllText(configPath, jsonString);
            
            AnsiConsole.MarkupLine($"[green]Success! Configuration saved to {Path.GetFullPath(configPath)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Configuration aborted. Nothing saved.[/]");
        }
    }
}

class RootConfig { public SoulmanConfig? Soulman { get; set; } }
