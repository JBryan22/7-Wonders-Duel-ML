using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using SevenWondersDuel.Advisor;

const string Usage = """
    usage: 7wd-advisor [--port N] [--data DIR] [--no-browser]
      Starts the Advisor Window on http://localhost:N (default 7700) and opens it in the browser.
      --data DIR    where the game in progress and earlier games are saved
                    (default: %LOCALAPPDATA%\7wd\advisor on Windows, ~/.local/share/7wd/advisor on Linux)
      --no-browser  only start the server (open the address yourself)
    Stop it with Ctrl+C or by closing this window; the game is saved after every entry.
    """;

var port = AdvisorServer.DefaultPort;
var data = AdvisorStore.DefaultFolder;
var browser = true;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536:
            port = p;
            i++;
            break;
        case "--data" when i + 1 < args.Length:
            data = args[++i];
            break;
        case "--no-browser":
            browser = false;
            break;
        default:
            Console.Error.WriteLine(Usage);
            return 1;
    }
}

var app = AdvisorServer.Build(new AdvisorOptions(data, port));
try
{
    await app.StartAsync();
}
catch (IOException e) when (e.InnerException is SocketException || e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Port {port} is in use: the Advisor may already be running (open http://localhost:{port}), or start it with --port N.");
    return 1;
}

var url = $"http://localhost:{port}/";
Console.WriteLine($"7wd Advisor Window: {url}");
Console.WriteLine($"Games are saved in {Path.GetFullPath(data)}");
Console.WriteLine("Press Ctrl+C to stop.");
if (browser) OpenBrowser(url);
await app.WaitForShutdownAsync();
return 0;

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
        else Process.Start("xdg-open", url);
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        Console.WriteLine($"Could not open a browser ({e.Message}); open {url} yourself.");
    }
}
