using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace Akiba.Desktop;

public partial class App : Application
{
    private const string ServerUrl = "http://localhost:5280";

    // Non-null only when WE started the server; null when the Windows Service was already running.
    private Process? _serverProcess;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        // If the Windows Service is already running, just attach to it.
        if (!await IsServerReadyAsync())
        {
            _serverProcess = StartServer();

            if (_serverProcess is null)
            {
                MessageBox.Show(
                    "Akiba.Web.exe was not found next to this application.\n\n" +
                    "Both files must be in the same folder.\n\n" +
                    $"Expected: {Path.Combine(AppContext.BaseDirectory, "Akiba.Web.exe")}",
                    "Akiba — Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            mainWindow.SetStatus("Starting server…");
            await WaitForServerAsync();
        }

        mainWindow.NavigateTo(ServerUrl);
    }

    private static Process? StartServer()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Akiba.Web.exe");

        if (!File.Exists(exe))
        {
            return null;
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static async Task<bool> IsServerReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var r = await http.GetAsync(ServerUrl + "/health");
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForServerAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                var r = await http.GetAsync(ServerUrl + "/health");
                if (r.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch { }

            await Task.Delay(500);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Kill the server only if we started it — don't touch a Windows Service.
        if (_serverProcess is { HasExited: false })
        {
            try
            {
                _serverProcess.Kill(entireProcessTree: true);
            }
            catch { }
        }

        base.OnExit(e);
    }
}
