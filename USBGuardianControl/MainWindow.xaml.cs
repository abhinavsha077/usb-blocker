using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace USBGuardianControl
{
    public class AgentViewModel
    {
        public string MachineId   { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string IpAddress   { get; set; } = string.Empty;
        public bool   IsLocked    { get; set; }
        public bool   Online      { get; set; }
        public string OnlineIcon  => Online ? "●" : "○";
        public string OnlineColor => Online ? "#107C10" : "#A19F9D";
        public string StatusLabel => IsLocked ? "LOCKED" : "OPEN";
        public string StatusBg    => IsLocked ? "#D13438" : "#107C10";
    }

    public class UsbDeviceViewModel
    {
        public string DeviceID   { get; set; } = string.Empty;
        public string Name       { get; set; } = string.Empty;
        public string Status     { get; set; } = string.Empty;
        public bool   IsAllowed  { get; set; }
        public bool   IsHub      { get; set; }
        public string ConnectedDeviceName { get; set; } = string.Empty;

        public string PolicyText  => IsAllowed ? "ALLOWED" : "BLOCKED";
        public string PolicyColor => IsAllowed ? "#107C10" : "#D13438";
        public Visibility ShowAllow => IsAllowed ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ShowBlock => IsAllowed ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility HubRowVisibility    => IsHub  ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeviceRowVisibility => !IsHub ? Visibility.Visible : Visibility.Collapsed;
        // Use policy state (IsAllowed) not transient WMI Status — the hardware status lags
        // behind by several seconds after a pnputil enable/disable cycle.
        public string ConnectedLabel => IsAllowed ? "● CONNECTED" : "○ BLOCKED";
        public string ConnectedColor => IsAllowed ? "#107C10"     : "#D13438";
    }

    public partial class MainWindow : Window
    {
        private readonly ServerClient _server = new();
        private DispatcherTimer? _refreshTimer;
        private string _selectedMachineId = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            // Timer starts stopped — only begins after successful Connect
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += async (_, _) =>
            {
                if (_server.IsConfigured)
                    await SafeRefreshAsync(silent: true);
            };
        }

        // ── Connect ──────────────────────────────────────────────────────────
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _server.Configure(txtServerUrl.Text, txtPassword.Password);
                await RefreshAsync();
                _refreshTimer.Start(); // only start after first successful configure
            }
            catch (Exception ex)
            {
                SetStatus($"Connect error: {ex.Message}");
            }
        }

        // ── Agent list ───────────────────────────────────────────────────────
        private async Task RefreshAsync(bool silent = false)
        {
            await SafeRefreshAsync(silent);
        }

        private async Task SafeRefreshAsync(bool silent = false)
        {
            try
            {
                var agents = await _server.GetAgentsAsync();
                if (agents == null)
                {
                    if (!silent) SetStatus("Cannot reach server. Check URL, password, and that the server service is running.");
                    return;
                }

                var vms = agents.Select(a => new AgentViewModel
                {
                    MachineId   = a.MachineId,
                    MachineName = a.MachineName,
                    IpAddress   = a.IpAddress,
                    IsLocked    = a.IsLocked,
                    Online      = a.Online
                }).ToList();

                lbAgents.ItemsSource = vms;
                lblAgentCount.Text   = vms.Count.ToString();

                if (!string.IsNullOrEmpty(_selectedMachineId))
                {
                    var sel = vms.FirstOrDefault(v => v.MachineId == _selectedMachineId);
                    if (sel != null) lbAgents.SelectedItem = sel;
                    await RefreshDevicesAsync(silent);
                }

                if (!silent) SetStatus($"Connected. {vms.Count} agent(s) found.");
            }
            catch (Exception ex)
            {
                if (!silent) SetStatus($"Error: {ex.Message}");
            }
        }

        private async void LbAgents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lbAgents.SelectedItem is not AgentViewModel vm) return;
            _selectedMachineId          = vm.MachineId;
            lblSelectedMachine.Text     = $"🖥  {vm.MachineName}";
            lblMachineStatus.Text       = $"IP: {vm.IpAddress}  |  {(vm.IsLocked ? "🔴 LOCKED" : "🟢 OPEN")}  |  {(vm.Online ? "Online" : "Offline")}";
            await RefreshDevicesAsync();
        }

        private async Task RefreshDevicesAsync(bool silent = false)
        {
            if (string.IsNullOrEmpty(_selectedMachineId)) return;
            var devices = await _server.GetDevicesAsync(_selectedMachineId);
            if (devices == null) { if (!silent) SetStatus("No data from agent."); return; }

            lvDevices.ItemsSource = devices.Select(d => new UsbDeviceViewModel
            {
                DeviceID   = d.DeviceID,
                Name       = d.Name,
                Status     = d.Status,
                IsAllowed  = d.IsAllowed,
                IsHub      = d.IsHub,
                ConnectedDeviceName = d.ConnectedDeviceName
            }).ToList();
        }

        // ── Per-machine lock/unlock ───────────────────────────────────────────
        private async void BtnLock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            await _server.LockAsync(_selectedMachineId);
            await RefreshAsync();
        }

        private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            await _server.UnlockAsync(_selectedMachineId);
            await RefreshAsync();
        }

        private async void BtnTimedUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (!int.TryParse(txtMinutes.Text, out int mins) || mins <= 0) { SetStatus("Enter a valid number of minutes."); return; }
            await _server.UnlockTimedAsync(_selectedMachineId, mins);
            SetStatus($"Timed unlock for {mins} min sent to {_selectedMachineId}.");
            await RefreshAsync();
        }

        // ── Global lock/unlock ────────────────────────────────────────────────
        private async void BtnLockAll_Click(object sender, RoutedEventArgs e)
        {
            await _server.LockAllAsync();
            SetStatus("Lock All sent to all agents.");
            await RefreshAsync();
        }

        private async void BtnUnlockAll_Click(object sender, RoutedEventArgs e)
        {
            await _server.UnlockAllAsync();
            SetStatus("Unlock All sent to all agents.");
            await RefreshAsync();
        }

        // ── Granular hub control ─────────────────────────────────────────────
        private async void BtnAllowDevice_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (sender is Button b && b.Tag is string id)
            {
                await _server.AllowAsync(_selectedMachineId, id);
                await RefreshDevicesAsync();
            }
        }

        private async void BtnBlockDevice_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (sender is Button b && b.Tag is string id)
            {
                await _server.BlockAsync(_selectedMachineId, id);
                await RefreshDevicesAsync();
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        // ── Helpers ──────────────────────────────────────────────────────────
        private bool NoMachine()
        {
            if (string.IsNullOrEmpty(_selectedMachineId)) { SetStatus("Please select a machine first."); return true; }
            return false;
        }

        private void SetStatus(string msg) => lblStatus.Text = msg;
    }
}
