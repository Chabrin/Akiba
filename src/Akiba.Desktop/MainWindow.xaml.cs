using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace Akiba.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            try { WebView.Dispose(); }
            catch { /* Dispose is best-effort on close */ }
        };
    }

    public void SetStatus(string message) =>
        Dispatcher.Invoke(() => StatusLabel.Text = message);

    public async void NavigateTo(string url)
    {
        // Store WebView2 data (cookies, cache) in LocalAppData so it persists
        // across app restarts and isn't wiped when the app folder is replaced.
        var cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Akiba", "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: cacheFolder);
        await WebView.EnsureCoreWebView2Async(env);

        // Strip browser chrome that would confuse users (no right-click "Inspect",
        // no address bar hints, no status bar URL flicker).
        var settings = WebView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;

        // Open target="_blank" links in the system browser rather than dropping them.
        WebView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };

        WebView.NavigationCompleted += OnFirstNavigationCompleted;
        WebView.CoreWebView2.Navigate(url);
    }

    private void OnFirstNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        WebView.NavigationCompleted -= OnFirstNavigationCompleted;
        Dispatcher.Invoke(FadeSplashOut);
    }

    private void FadeSplashOut()
    {
        var anim = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(350)));
        anim.Completed += (_, _) => SplashOverlay.Visibility = Visibility.Collapsed;
        SplashOverlay.BeginAnimation(OpacityProperty, anim);
    }
}
