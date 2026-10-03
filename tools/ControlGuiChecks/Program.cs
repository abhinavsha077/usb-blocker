using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using USBGuardianControl;

// Runs the real WPF views without showing a window or touching a live server.
// A deterministic in-memory HTTP handler exercises the same refresh/action paths.
internal static class Program
{
    private static int _checks;
    private static int _exitCode;
    private static string _output = string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/gui");
        Directory.CreateDirectory(_output);
        var app = new App();
        app.InitializeComponent();
        // Pump only the Dispatcher; Application.Run would open the production StartupUri.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(); Console.WriteLine($"PASS: {_checks} GUI checks. Renders: {_output}"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); _exitCode = 1; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return _exitCode;
    }

    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing control: {name}");
    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("GUI did not settle.");
    }

    private static async Task RunAsync()
    {
        var window = new MainWindow();
        var server = (ServerClient)Field(window, "_server");
        var fake = new FakeServer();
        server.Configure("http://gui-check.invalid", "test-only-password");
        ((HttpClient)Field(server, "_http")).Dispose();
        SetField(server, "_http", new HttpClient(fake) { BaseAddress = new Uri("http://gui-check.invalid/") });
        SetField(window, "_configuredServerUrl", "http://gui-check.invalid");
        var list = Control<ListBox>(window, "lbAgents");
        var devices = Control<ListView>(window, "lvDevices");
        var tabs = Control<TabControl>(window, "workspaceTabs");
        var search = Control<TextBox>(window, "txtEndpointSearch");
        var filter = Control<ComboBox>(window, "cmbEndpointFilter");
        var hubs = Control<CheckBox>(window, "chkShowInfrastructure");
        Check(tabs.SelectedIndex == 0, "Device workspace must be selected at startup.");
        Check(!Control<Button>(window, "btnLock").IsEnabled, "Commands must start disabled.");
        await RenderAsync(window, "01-disconnected", 1440, 900);
        var inputViewport = FindVisual<ScrollViewer>(Control<TextBox>(window, "txtServerUrl"));
        Check(inputViewport != null && inputViewport.ViewportHeight >= 16, "Connection input text must not be clipped by the field template.");

        Check(await (Task<bool>)Invoke(window, "SafeRefreshAsync", false)!, "Fleet refresh failed.");
        Check(list.Items.Count == 5, "All endpoints must appear.");
        Check(Control<TextBlock>(window, "lblOnline").Text == "4", "Online count incorrect.");
        Check(Control<TextBlock>(window, "lblAttention").Text == "2", "Attention must include pending/error states.");
        list.SelectedIndex = 0;
        await UntilAsync(() => devices.Items.Count == 3);
        Check(Control<Button>(window, "btnReload").IsEnabled, "Online endpoint must support maintenance.");
        Check(devices.Items.Cast<UsbDeviceViewModel>().All(d => !d.IsHub), "Infrastructure must be hidden by default.");
        Control<Expander>(window, "connectionPanel").IsExpanded = false;
        await RenderAsync(window, "02-devices", 1440, 900);
        await RenderAsync(window, "03-small-window", 1080, 680);
        hubs.IsChecked = true;
        Check(devices.Items.Count == 4, "Infrastructure toggle must show hub.");
        var hub = devices.Items.Cast<UsbDeviceViewModel>().Single(d => d.IsHub);
        Check(hub.ShowAllow == Visibility.Collapsed && hub.ShowBlock == Visibility.Collapsed, "Hubs must never expose policy actions.");
        hubs.IsChecked = false;
        Control<Button>(window, "navPolicy").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(tabs.SelectedIndex == 1, "Policy navigation must switch workspace.");
        Check(Control<Border>(window, "fleetSummary").Visibility == Visibility.Collapsed, "Policy workspace must prioritize controls over fleet summary.");
        await RenderAsync(window, "04-access-policy", 1440, 900);
        var policyScroll = (ScrollViewer)tabs.SelectedContent;
        policyScroll.ScrollToEnd();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(policyScroll.VerticalOffset > 0, "Policy controls below the fold must be reachable with the custom scrollbar.");
        await RenderAsync(window, "04b-policy-controls", 1440, 900);
        policyScroll.ScrollToHome();
        Control<Button>(window, "navAdministration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(tabs.SelectedIndex == 2, "Administration navigation must switch workspace.");
        await RenderAsync(window, "05-administration", 1440, 900);
        Control<Button>(window, "navDevices").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(tabs.SelectedIndex == 0, "Device navigation must switch workspace.");
        Check(Control<Border>(window, "fleetSummary").Visibility == Visibility.Visible, "Device workspace must restore the fleet summary.");

        var lockButton = Control<Button>(window, "btnLock");
        Invoke(window, "BtnLock_Click", lockButton, new RoutedEventArgs());
        Check(!lockButton.IsEnabled && !list.IsEnabled, "Commands must disable duplicate actions and endpoint switching while pending.");
        await UntilAsync(() => !((bool)Field(window, "_actionInProgress")));
        Check(fake.Posts.Single().EndsWith("/HQ-WS-01/lock", StringComparison.Ordinal), "Action must target selected machine.");
        Check(list.IsEnabled, "Endpoint switching must be restored after action.");

        list.SelectedIndex = 1;
        list.SelectedIndex = 2;
        await UntilAsync(() => (string)Field(window, "_deviceListMachineId") == "HQ-WS-03");
        Check((string)Field(window, "_selectedMachineId") == "HQ-WS-03", "A late inventory response must not change the endpoint target.");
        filter.SelectedIndex = 1;
        Check(list.Items.Count == 4, "Online filter incorrect.");
        Check(Control<TextBlock>(window, "lblTotal").Text == "5", "Filtering must not change fleet totals.");
        filter.SelectedIndex = 0;
        list.SelectedIndex = 0;
        await UntilAsync(() => (string)Field(window, "_deviceListMachineId") == "HQ-WS-01");

        search.Text = "Finance";
        Check(list.Items.Count == 2, "Group search must filter endpoints.");
        Check(list.SelectedItem == null && !Control<Button>(window, "btnLock").IsEnabled, "Hidden selection must clear command target.");
        search.Text = "10.20.0.12";
        Check(list.Items.Count == 1, "IP search must filter endpoints.");
        search.Text = "no-such-endpoint";
        Check(Control<StackPanel>(window, "pnlNoAgents").Visibility == Visibility.Visible, "No-match state missing.");
        search.Text = "";
        filter.SelectedIndex = 2;
        Check(list.Items.Count == 1, "Offline filter incorrect.");
        list.SelectedIndex = 0;
        await UntilAsync(() => Control<StackPanel>(window, "pnlOffline").Visibility == Visibility.Visible);
        Check(Control<Button>(window, "btnLock").IsEnabled, "Offline endpoint policy must remain editable.");
        Check(!Control<Button>(window, "btnReboot").IsEnabled, "Offline power actions must be disabled.");
        tabs.SelectedIndex = 0;
        await RenderAsync(window, "06-offline", 1440, 900);
        filter.SelectedIndex = 3;
        Check(list.Items.Count == 2, "Attention filter incorrect.");
        filter.SelectedIndex = 0;
        list.SelectedIndex = 0;
        await UntilAsync(() => devices.Items.Count == 3);
        fake.Fail = true;
        Check(!await (Task<bool>)Invoke(window, "SafeRefreshAsync", true)!, "Failed request must report failure.");
        Check(!Control<Button>(window, "btnLock").IsEnabled && !devices.IsEnabled, "Disconnected state must disable policy/device actions.");
        Check(Control<TextBlock>(window, "lblTotal").Text == "—", "Disconnected fleet must not claim current counts.");
        Check(Control<Expander>(window, "connectionPanel").IsExpanded, "Connection failure must expose reconnect form.");
        await RenderAsync(window, "07-connection-error", 1080, 680);
        fake.Fail = false;
        Check(await (Task<bool>)Invoke(window, "SafeRefreshAsync", false)!, "Reconnect refresh must recover.");
        Check(Control<Button>(window, "btnLock").IsEnabled, "Recovered connection must restore endpoint actions.");
        fake.Fail = true;
        Invoke(window, "BtnLock_Click", lockButton, new RoutedEventArgs());
        await UntilAsync(() => !((bool)Field(window, "_actionInProgress")));
        Check(!lockButton.IsEnabled && !devices.IsEnabled, "Failed policy command must keep actions disabled after the busy state clears.");
        fake.Fail = false;
        await (Task<bool>)Invoke(window, "SafeRefreshAsync", false)!;

        var history = new HistoryWindow(server, "HQ-WS-01", "HQ-WS-01");
        await (Task)Invoke(history, "LoadAsync")!;
        Check(Control<ListView>(history, "lvHistory").Items.Count == 2, "History must display server events.");
        await RenderAsync(history, "08-history", 1000, 580);
        fake.Fail = true;
        await (Task)Invoke(history, "LoadAsync")!;
        Check(Control<TextBlock>(history, "lblEmptyTitle").Text == "Unable to load event history", "History error must not claim there are no events.");
        fake.Fail = false;
        var password = new ChangePasswordWindow(server, "test-only-password");
        Control<PasswordBox>(password, "txtNewPwd").Password = "short";
        Invoke(password, "BtnConfirm_Click", password, new RoutedEventArgs());
        Check(Control<TextBlock>(password, "lblError").Text.Contains("8 characters"), "Password minimum must be validated.");
        Control<PasswordBox>(password, "txtNewPwd").Password = "long-enough-password";
        Control<PasswordBox>(password, "txtConfirmPwd").Password = "different-password";
        Invoke(password, "BtnConfirm_Click", password, new RoutedEventArgs());
        Check(Control<TextBlock>(password, "lblError").Text == "Passwords do not match.", "Password confirmation must be validated.");
        await RenderAsync(password, "09-password", 480, 440);
        ((DispatcherTimer)Field(window, "_refreshTimer")).Stop();
        server.Dispose();
    }

    private static async Task RenderAsync(Window window, string name, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, name + ".png"));
        encoder.Save(stream);
        Check(root.ActualWidth + root.Margin.Left + root.Margin.Right == width &&
            root.ActualHeight + root.Margin.Top + root.Margin.Bottom == height, $"{name}: incorrect viewport.");
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindVisual<T>(VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public List<string> Posts { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken);
            if (Fail) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (request.Method == HttpMethod.Post)
            {
                Posts.Add(path);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Policy saved.") };
            }
            if (path.EndsWith("/devices", StringComparison.Ordinal)) body = new[]
            {
                new UsbDeviceDto { DeviceID = @"USBSTOR\DISK&VEN_SANDISK\1234", Name = "SanDisk Ultra USB drive", IsAllowed = true, IsRunning = true, Status = "OK" },
                new UsbDeviceDto { DeviceID = @"USBSTOR\DISK&VEN_KINGSTON\5678", Name = "Kingston DataTraveler", IsRunning = false, Status = "Error" },
                new UsbDeviceDto { DeviceID = @"USB\VID_18D1&PID_4EE1\ABC", Name = "Portable device with a long descriptive manufacturer and product name", IsRunning = null },
                new UsbDeviceDto { DeviceID = @"USB\ROOT_HUB30\0", Name = "USB Root Hub (USB 3.0)", IsHub = true, IsRunning = true }
            };
            else if (path.EndsWith("/history", StringComparison.Ordinal)) body = new[]
            {
                new UsbEventDto { Timestamp = DateTime.UtcNow, EventType = "Blocked", DeviceName = "Kingston DataTraveler", Detail = "Device blocked by endpoint policy." },
                new UsbEventDto { Timestamp = DateTime.UtcNow.AddMinutes(-4), EventType = "Authorized", DeviceName = "SanDisk Ultra", Detail = "Allow policy applied." }
            };
            else body = new[]
            {
                Agent("HQ-WS-01", "10.20.0.11", "Operations"),
                Agent("HQ-WS-02", "10.20.0.12", "Finance", locked: false),
                Agent("HQ-WS-03", "10.20.0.13", "Finance", pending: true),
                Agent("HQ-WS-04", "10.20.0.14", "Operations", error: "Device disable failed."),
                Agent("HQ-WS-05", "10.20.0.15", "Engineering", online: false)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
        }
        private static AgentSummary Agent(string name, string ip, string group, bool online = true, bool locked = true, bool pending = false, string error = "") =>
            new() { MachineId = name, MachineName = name, IpAddress = ip, Group = group, Online = online,
                IsLocked = locked, PolicyRevision = 4, AppliedRevision = pending ? 3 : 4, EnforcementUtc = DateTime.UtcNow,
                EnforcementError = error, LastSeen = online ? DateTime.UtcNow : DateTime.UtcNow.AddHours(-2) };
    }
}
