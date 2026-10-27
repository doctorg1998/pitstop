using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Pitstop.Core.Dashboard;
using Pitstop.Windows;

namespace Pitstop.App;

/// <summary>
/// The dashboard window: the embedded HTML UI in WebView2, fed with app state and live metrics.
/// Locked down: it serves only its own embedded page from a private origin, blocks every other navigation
/// and new window, and treats every message from the page as untrusted (see <see cref="DashboardCommandParser"/>).
/// Closing the window disposes WebView2, so the browser processes and the metrics sampler stop with it.
/// </summary>
internal sealed class DashboardForm : Form
{
    private const string Origin = "https://app.pitstop";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x0d, 0x11, 0x17) };
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 1000 };
    private readonly MetricsSampler _sampler = new();
    private readonly Func<object> _state;
    private readonly Action<DashboardCommand> _onCommand;
    private bool _ready;

    public DashboardForm(Func<object> state, Action<DashboardCommand> onCommand)
    {
        _state = state;
        _onCommand = onCommand;
        Text = "Pitstop";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x0d, 0x11, 0x17);
        ClientSize = LogicalToDeviceUnits(new Size(1320, 860));
        MinimumSize = LogicalToDeviceUnits(new Size(1120, 720));
        Controls.Add(_web);
        _metricsTimer.Tick += (_, _) => PushMetrics();
        Load += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataFolder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            TrayHost.Log("Dashboard unavailable: WebView2 runtime not installed.");
            MessageBox.Show(this, "The dashboard needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11.\n\n" +
                "Install it from microsoft.com/edge/webview2, or keep using Pitstop from the tray menu.",
                "Pitstop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = Serve(core.Environment, e.Request.Uri);
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.Navigate(Origin + "/");
    }

    private static CoreWebView2WebResourceResponse Serve(CoreWebView2Environment env, string uri)
    {
        var path = new Uri(uri).AbsolutePath;
        if (path is "/" or "/index.html")
        {
            var stream = typeof(DashboardForm).Assembly.GetManifestResourceStream("Pitstop.Dashboard.html")!;
            return env.CreateWebResourceResponse(stream, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        }
        return env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
        var cmd = DashboardCommandParser.Parse(e.WebMessageAsJson, out var rejection);
        if (cmd is null)
        {
            TrayHost.Log("Dashboard message rejected: " + rejection);
            return;
        }
        if (cmd.Type == "ready")
        {
            _ready = true;
            PushState();
            PushMetrics();
            _metricsTimer.Start();
            return;
        }
        _onCommand(cmd);
    }

    public void PushState() => Post(_state());

    public void Toast(string text, string kind = "") => Post(new { type = "toast", text, kind });

    private void PushMetrics()
    {
        var m = _sampler.Sample();
        Post(new { type = "metrics", t = m.At, cpu = m.CpuPercent, mem = m.MemoryPercent, down = m.NetDownKBps, up = m.NetUpKBps, procs = m.ProcessCount });
    }

    private void Post(object message)
    {
        if (!_ready || IsDisposed || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    /// <summary>Brings the window back if it's minimized or behind other windows.</summary>
    public void Reveal()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1; // dark title bar to match the dashboard (Windows 10 20H1+ / 11)
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _metricsTimer.Stop();
        _ready = false;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _metricsTimer.Dispose();
            _web.Dispose(); // shuts down the WebView2 browser processes
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
