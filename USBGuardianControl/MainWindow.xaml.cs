using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        public DateTime? LastSeen { get; set; }
        public bool   LearnMode   { get; set; }
        public string Group       { get; set; } = "Default";
        public bool   OpticalBlocked   { get; set; }
        public bool   BluetoothBlocked { get; set; }
        public bool   WifiBlocked      { get; set; }
        public bool   BadUsbBlock      { get; set; }
        public DateTime? UnlockExpiry  { get; set; }
        public DateTime? LearnModeExpiry { get; set; }
        public long PolicyRevision { get; set; }
        public long? AppliedRevision { get; set; }
        public DateTime? EnforcementUtc { get; set; }
        public string EnforcementError { get; set; } = string.Empty;
        public string OnlineIcon  => Online ? "●" : "○";
        public string OnlineColor => Online ? "#107C10" : "#A19F9D";
        public string OnlineLabel => Online
            ? "Online"
            : LastSeen.HasValue
                ? $"Last seen {LastSeen.Value.ToLocalTime():g}"
                : "Offline - never connected";
        public string StatusLabel => !string.IsNullOrEmpty(EnforcementError) ? "Enforcement error" :
            !Online ? "Offline · Saved policy: " + (IsLocked ? "locked" : "open") :
            AppliedRevision != PolicyRevision ? "Policy pending · Awaiting endpoint verification" :
            !EnforcementUtc.HasValue || DateTime.UtcNow - EnforcementUtc.Value > TimeSpan.FromSeconds(30)
                ? "Verification stale · Awaiting a fresh endpoint report" :
            LearnMode ? "Learn mode active" : IsLocked ? "Lock policy verified" : "Open policy verified";
        public bool NeedsAttention => !string.IsNullOrEmpty(EnforcementError) ||
            (Online && (AppliedRevision != PolicyRevision || !EnforcementUtc.HasValue ||
                DateTime.UtcNow - EnforcementUtc.Value > TimeSpan.FromSeconds(30)));
        public string ShortStatus => !string.IsNullOrEmpty(EnforcementError) ? "Enforcement error" :
            !Online ? "Offline" : AppliedRevision != PolicyRevision ? "Policy pending" :
            NeedsAttention ? "Verification stale" : LearnMode ? "Learning" : IsLocked ? "Lock verified" : "Open verified";
        public string StatusBg => !string.IsNullOrEmpty(EnforcementError) ? "#B42336" :
            !Online ? "#59687E" : NeedsAttention || LearnMode || !IsLocked ? "#965A08" : "#255CD6";
        public string StatusTint => !string.IsNullOrEmpty(EnforcementError) ? "#FDECEF" :
            !Online ? "#EDF0F5" : NeedsAttention || LearnMode || !IsLocked ? "#FFF3DF" : "#EDF3FF";
        public string EndpointSubtitle => $"{IpAddress} · {Group}";
    }

    public class UsbDeviceViewModel
    {
        public string DeviceID            { get; set; } = string.Empty;
        public string Name                { get; set; } = string.Empty;
        public string Status              { get; set; } = string.Empty;
        public bool   IsAllowed           { get; set; }
        public bool   IsHub               { get; set; }
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public bool? IsRunning { get; set; }
        public string EnforcementError { get; set; } = string.Empty;

        public string     PolicyText  => IsHub ? "Display only" : IsAllowed ? "Allow" : "Block";
        public string DeviceKind => IsHub ? "USB INFRASTRUCTURE" : "REMOVABLE DEVICE";
        public string StateTint => !string.IsNullOrEmpty(EnforcementError) ? "#FCEEF1" : IsRunning == true ? "#E8F7F1" : "#F0F3F8";
        public string     PolicyColor => IsHub ? "#5E718D" : IsAllowed ? "#137F67" : "#C13949";
        public Visibility ShowAllow   => !IsHub && !IsAllowed ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowBlock   => !IsHub && IsAllowed  ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HubRowVisibility    => IsHub  ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeviceRowVisibility => !IsHub ? Visibility.Visible : Visibility.Collapsed;
        public string ConnectedLabel => !string.IsNullOrEmpty(EnforcementError) ? "Enforcement error" :
            IsRunning == null ? "Unknown" : IsRunning == true ? "Enabled" : "Disabled / faulted";
        public string ConnectedColor => !string.IsNullOrEmpty(EnforcementError) ? "#C13949" :
            IsRunning == true ? "#137F67" : "#5E718D";
    }

    public partial class MainWindow : Window
    {
        private readonly ServerClient    _server   = new();
        private readonly ControlSettings _settings = ControlSettings.Load();
        private DispatcherTimer? _refreshTimer;
        private string _selectedMachineId = string.Empty;
        private bool _refreshInProgress;
        private bool _restoringSelection;
        private long _deviceRequestVersion;
        private string _deviceListMachineId = string.Empty;
        private List<AgentViewModel> _agents = new();
        private List<UsbDeviceViewModel> _devices = new();
        private bool _isConnected;
        private bool _connectionInProgress;
        private bool _actionInProgress;
        private string _configuredServerUrl = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            txtServerUrl.Text = _settings.ServerUrl;
            UpdateNavigation();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += async (_, _) =>
            {
                if (_server.IsConfigured) await SafeRefreshAsync(silent: true);
            };
        }

        // ── Connection ───────────────────────────────────────────────────────
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_connectionInProgress || _actionInProgress) return;
            if (_refreshInProgress)
            {
                SetStatus("A refresh is running. Try connecting again in a moment.");
                return;
            }
            _connectionInProgress = true;
            btnConnect.IsEnabled = false;
            txtServerUrl.IsEnabled = txtPassword.IsEnabled = false;
            btnChangePassword.IsEnabled = false;
            btnConnect.Content = "Connecting…";
            try
            {
                _refreshTimer!.Stop();
                _server.Configure(txtServerUrl.Text.Trim(), txtPassword.Password);
                _configuredServerUrl = txtServerUrl.Text.Trim();
                SetConnected(false);
                ClearSelection();
                _agents.Clear();
                ApplyEndpointFilter();
                SetStatus("Connecting to server…");

                if (!await RefreshAsync()) return;
                _settings.ServerUrl = _configuredServerUrl;
                _settings.Save();
                _refreshTimer!.Start();

                SetConnected(true, _configuredServerUrl);
                SetGlobalButtonsEnabled(true);
                connectionPanel.IsExpanded = false;
            }
            catch (Exception ex)
            {
                SetStatus($"Connect failed: {ex.Message}", StatusKind.Error);
                SetConnected(false);
            }
            finally
            {
                _connectionInProgress = false;
                btnConnect.IsEnabled = true;
                txtServerUrl.IsEnabled = txtPassword.IsEnabled = true;
                btnChangePassword.IsEnabled = true;
                btnConnect.Content = _isConnected ? "Reconnect" : "Connect";
            }
        }

        private void Connection_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            BtnConnect_Click(sender, e);
        }

        private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (overviewHeading != null)
                overviewHeading.Visibility = e.NewSize.Height < 780 ? Visibility.Collapsed : Visibility.Visible;
            if (fleetSummary == null || lblTotalSubtitle == null) return;
            bool compact = e.NewSize.Height < 780;
            fleetSummary.Padding = compact ? new Thickness(18, 10, 18, 8) : new Thickness(22, 14, 22, 14);
            foreach (var subtitle in new[] { lblTotalSubtitle, lblOnlineSubtitle, lblOfflineSubtitle, lblAttentionSubtitle })
                subtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Navigation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int page))
                workspaceTabs.SelectedIndex = page;
        }

        private void ConnectionNavigation_Click(object sender, RoutedEventArgs e)
        {
            connectionPanel.IsExpanded = !connectionPanel.IsExpanded;
            if (connectionPanel.IsExpanded) txtServerUrl.Focus();
        }

        private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (workspaceTabs != null && e.Source == workspaceTabs) UpdateNavigation();
        }

        private void UpdateNavigation()
        {
            if (navDevices == null || navPolicy == null || navAdministration == null) return;
            fleetSummary.Visibility = workspaceTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            var buttons = new[] { navDevices, navPolicy, navAdministration };
            for (int i = 0; i < buttons.Length; i++)
            {
                bool selected = i == workspaceTabs.SelectedIndex;
                buttons[i].Background = new SolidColorBrush(selected ? Color.FromRgb(0x29, 0x43, 0x70) : Colors.Transparent);
                buttons[i].Foreground = new SolidColorBrush(selected ? Colors.White : Color.FromRgb(0x9B, 0xAF, 0xCD));
                System.Windows.Automation.AutomationProperties.SetItemStatus(buttons[i], selected ? "Selected" : "");
            }
            lblWorkspaceTitle.Text = workspaceTabs.SelectedIndex switch { 1 => "Access policies", 2 => "Endpoint management", _ => "Device control" };
            lblWorkspaceSubtitle.Text = workspaceTabs.SelectedIndex switch
            {
                1 => "Define trusted devices and control access across your fleet.",
                2 => "Organize endpoints, synchronize agents and manage maintenance.",
                _ => "Visibility and control for every connected device."
            };
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F5 || !_isConnected || _actionInProgress) return;
            e.Handled = true;
            await RefreshAsync();
        }

        private void BtnChangePassword_Click(object sender, RoutedEventArgs e)
        {
            if (!_server.IsConfigured) { SetStatus("Connect to the server first.", StatusKind.Warning); return; }
            var dialog = new ChangePasswordWindow(_server, txtPassword.Password) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                txtPassword.Password = dialog.NewPassword;
                SetStatus("Admin password changed.", StatusKind.Success);
            }
        }

        // ── Agent list ───────────────────────────────────────────────────────
        private Task<bool> RefreshAsync(bool silent = false) => SafeRefreshAsync(silent);

        private async Task<bool> SafeRefreshAsync(bool silent = false)
        {
            if (_refreshInProgress) return false;
            _refreshInProgress = true;
            try
            {
                var agents = await _server.GetAgentsAsync();

                var vms = agents.Select(a => new AgentViewModel
                {
                    MachineId   = a.MachineId,
                    MachineName = a.MachineName,
                    IpAddress   = a.IpAddress,
                    IsLocked    = a.IsLocked,
                    Online      = a.Online,
                    LastSeen    = a.LastSeen,
                    LearnMode   = a.LearnMode,
                    Group       = string.IsNullOrWhiteSpace(a.Group) ? "Default" : a.Group,
                    OpticalBlocked   = a.OpticalBlocked,
                    BluetoothBlocked = a.BluetoothBlocked,
                    WifiBlocked      = a.WifiBlocked,
                    BadUsbBlock      = a.BadUsbBlock,
                    UnlockExpiry     = a.UnlockExpiry,
                    LearnModeExpiry  = a.LearnModeExpiry,
                    PolicyRevision = a.PolicyRevision,
                    AppliedRevision = a.AppliedRevision,
                    EnforcementUtc = a.EnforcementUtc,
                    EnforcementError = a.EnforcementError
                }).ToList();

                _agents = vms;
                SetConnected(true, _configuredServerUrl);
                lblTotal.Text = vms.Count.ToString();
                lblOnline.Text = vms.Count(v => v.Online).ToString();
                lblOfflineCount.Text = vms.Count(v => !v.Online).ToString();
                lblAttention.Text = vms.Count(v => v.NeedsAttention).ToString();
                ApplyEndpointFilter();

                // Filtering restores the same identity without issuing a second device request.
                if (lbAgents.SelectedItem is AgentViewModel selected)
                {
                    UpdateMachineHeader(selected);
                    SetMachineButtonsEnabled(true, selected.Online);
                    await RefreshDevicesForAsync(selected);
                }

                SetConnected(true, _configuredServerUrl);
                SetGlobalButtonsEnabled(true);
                lblAutoRefresh.Text = $"Refreshes every 5s · Updated {DateTime.Now:HH:mm:ss}";
                if (!silent) SetStatus($"Connected — {vms.Count} managed machine(s) found.", StatusKind.Success);
                return true;
            }
            catch (Exception ex)
            {
                SetConnected(false);
                SetGlobalButtonsEnabled(false);
                SetMachineButtonsEnabled(false);
                SetStatus($"Unable to refresh: {ex.Message}", StatusKind.Error);
                return false;
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        private async void LbAgents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringSelection) return;
            if (lbAgents.SelectedItem is not AgentViewModel vm) return;
            _selectedMachineId = vm.MachineId;
            _deviceListMachineId = string.Empty;
            _devices.Clear();
            lvDevices.ItemsSource = null;
            _deviceRequestVersion++;
            UpdateMachineHeader(vm);
            deviceToolbar.Visibility = deviceTableHeader.Visibility = Visibility.Visible;
            SetMachineButtonsEnabled(true, vm.Online);
            if (!_isConnected)
            {
                SetConnected(false);
                return;
            }
            pnlNoMachine.Visibility = Visibility.Collapsed;
            pnlOffline.Visibility = Visibility.Collapsed;
            pnlNoDevices.Visibility = Visibility.Visible;
            lblDeviceEmptyTitle.Text = "Loading devices…";
            lblDeviceEmptyDescription.Text = "Waiting for the latest endpoint inventory.";
            await TryRefreshDevicesAsync(vm);
        }

        private void EndpointFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (lbAgents == null || cmbEndpointFilter == null) return;
            ApplyEndpointFilter();
        }

        private void ApplyEndpointFilter()
        {
            string search = txtEndpointSearch.Text.Trim();
            var visible = _agents.Where(v =>
                (search.Length == 0 || v.MachineName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                 v.MachineId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                 v.IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                 v.Group.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
                (cmbEndpointFilter.SelectedIndex switch
                {
                    1 => v.Online,
                    2 => !v.Online,
                    3 => v.NeedsAttention,
                    _ => true
                })).OrderBy(v => v.MachineName, StringComparer.OrdinalIgnoreCase).ToList();
            _restoringSelection = true;
            try
            {
                lbAgents.ItemsSource = visible;
                lbAgents.SelectedItem = visible.FirstOrDefault(v => v.MachineId == _selectedMachineId);
            }
            finally { _restoringSelection = false; }
            if (lbAgents.SelectedItem == null) ClearSelection();
            lblAgentCount.Text = $"{visible.Count} / {_agents.Count}";
            pnlNoAgents.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            lblEndpointEmptyTitle.Text = _agents.Count > 0 ? "No matching endpoints" : _isConnected ? "No endpoints yet" : "Connect your server";
            lblEndpointEmptyDescription.Text = _agents.Count > 0 ? "Change the search or status filter to see more endpoints." :
                _isConnected ? "Enrolled endpoints appear here when they register with the server." : "Enter your server address and admin password above to load endpoints.";
        }

        private void ClearSelection()
        {
            _selectedMachineId = _deviceListMachineId = string.Empty;
            _deviceRequestVersion++;
            _devices.Clear();
            lvDevices.ItemsSource = null;
            lblDeviceCount.Text = "Live device inventory";
            lblSelectedMachine.Text = "Endpoint workspace";
            lblSelectedMachine.ToolTip = null;
            lblMachineStatus.Text = "Select an endpoint to inspect devices and access policy.";
            lblVerification.Text = string.Empty;
            lblVerification.ToolTip = null;
            deviceToolbar.Visibility = deviceTableHeader.Visibility = Visibility.Collapsed;
            pnlNoMachine.Visibility = Visibility.Visible;
            pnlNoDevices.Visibility = pnlOffline.Visibility = Visibility.Collapsed;
            SetMachineButtonsEnabled(false);
        }

        private void UpdateMachineHeader(AgentViewModel vm)
        {
            lblSelectedMachine.Text = vm.MachineName;
            lblSelectedMachine.ToolTip = vm.MachineId;
            lblMachineStatus.Text = $"{vm.IpAddress}  ·  {vm.Group}  ·  {vm.OnlineLabel}";
            lblVerification.Text = vm.StatusLabel +
                (vm.UnlockExpiry.HasValue ? $" · Temporary access until {vm.UnlockExpiry.Value.ToLocalTime():g}" : "") +
                (vm.LearnMode && vm.LearnModeExpiry.HasValue ? $" · Learning until {vm.LearnModeExpiry.Value.ToLocalTime():g}" : "");
            lblVerification.Foreground = (Brush)new BrushConverter().ConvertFromString(vm.StatusBg)!;
            lblVerification.ToolTip = string.IsNullOrEmpty(vm.EnforcementError)
                ? $"Saved revision: {vm.PolicyRevision} · Applied revision: {vm.AppliedRevision?.ToString() ?? "unknown"}"
                : vm.EnforcementError;
            btnLearnMode.Content = vm.LearnMode ? "Disable learn mode" : "Enable learn mode";
            // Do not overwrite an administrator's in-progress group edit on each polling tick.
            if (!txtGroup.IsKeyboardFocusWithin) txtGroup.Text = vm.Group;
            btnSecOptical.Content = vm.OpticalBlocked ? "Allow optical drives" : "Block optical drives";
            btnSecBluetooth.Content = vm.BluetoothBlocked ? "Allow Bluetooth" : "Block Bluetooth";
            btnSecWifi.Content = vm.WifiBlocked ? "Allow Wi-Fi" : "Block Wi-Fi";
            btnBadUsb.Content = vm.BadUsbBlock ? "Disable BadUSB auto-block" : "Enable BadUSB auto-block";
        }

        private async Task RefreshDevicesAsync()
        {
            if (string.IsNullOrEmpty(_selectedMachineId)) return;

            string requestedMachine = _selectedMachineId;
            long requestVersion = ++_deviceRequestVersion;
            List<UsbDeviceDto> devices;
            try { devices = await _server.GetDevicesAsync(requestedMachine); }
            catch
            {
                if (requestVersion != _deviceRequestVersion || requestedMachine != _selectedMachineId) return;
                throw;
            }
            if (requestVersion != _deviceRequestVersion || requestedMachine != _selectedMachineId) return;

            var vms = devices.Select(d => new UsbDeviceViewModel
            {
                DeviceID            = d.DeviceID,
                Name                = d.Name,
                Status              = d.Status,
                IsAllowed           = d.IsAllowed,
                IsHub               = d.IsHub,
                ConnectedDeviceName = d.ConnectedDeviceName,
                IsRunning = d.IsRunning,
                EnforcementError = d.EnforcementError
            }).ToList();

            _devices = vms;
            _deviceListMachineId = requestedMachine;
            ApplyDeviceFilter();
            lvDevices.IsEnabled = _isConnected && !_actionInProgress;

            // Empty states
            pnlNoMachine.Visibility  = Visibility.Collapsed;
            pnlOffline.Visibility    = Visibility.Collapsed;
        }

        private void DeviceFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (lvDevices == null || chkShowInfrastructure == null) return;
            ApplyDeviceFilter();
        }

        private void ApplyDeviceFilter()
        {
            var visible = _devices.Where(d => chkShowInfrastructure.IsChecked == true || !d.IsHub).ToList();
            lvDevices.ItemsSource = visible;
            lblDeviceCount.Text = $"{_devices.Count(d => !d.IsHub)} devices · {_devices.Count(d => d.IsHub)} hubs / controllers";
            bool liveInventory = _isConnected && _deviceListMachineId == _selectedMachineId && !string.IsNullOrEmpty(_selectedMachineId);
            pnlNoDevices.Visibility = liveInventory && visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            lblDeviceEmptyTitle.Text = chkShowInfrastructure.IsChecked == true ? "No devices reported" : "No removable devices";
            lblDeviceEmptyDescription.Text = _devices.Any(d => d.IsHub)
                ? "Only USB infrastructure was reported. Select ‘Show hubs and controllers’ to inspect it."
                : "No devices were reported in the latest endpoint scan.";
        }

        private async Task RefreshDevicesForAsync(AgentViewModel vm)
        {
            if (!vm.Online)
            {
                _deviceRequestVersion++;
                _deviceListMachineId = string.Empty;
                _devices.Clear();
                lvDevices.ItemsSource = null;
                lblDeviceCount.Text = "Live inventory unavailable";
                pnlNoMachine.Visibility = Visibility.Collapsed;
                pnlNoDevices.Visibility = Visibility.Collapsed;
                pnlOffline.Visibility = Visibility.Visible;
                return;
            }

            await RefreshDevicesAsync();
        }

        private Task TryRefreshDevicesAsync() =>
            lbAgents.SelectedItem is AgentViewModel vm
                ? TryRefreshDevicesAsync(vm)
                : Task.CompletedTask;

        private async Task TryRefreshDevicesAsync(AgentViewModel vm)
        {
            string requestedMachine = vm.MachineId;
            try
            {
                await RefreshDevicesForAsync(vm);
            }
            catch (Exception ex)
            {
                if (requestedMachine != _selectedMachineId) return;
                HandleOperationError(ex);
            }
        }

        // ── Per-machine lock / unlock ─────────────────────────────────────────
        private async void BtnLock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            await ExecuteActionAsync(
                () => _server.LockAsync(_selectedMachineId),
                $"Lock policy saved for {_selectedMachineId}; awaiting endpoint verification.");
        }

        private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            await ExecuteActionAsync(
                () => _server.UnlockAsync(_selectedMachineId),
                $"Unlock policy saved for {_selectedMachineId}; awaiting endpoint verification.");
        }

        // ── Auto-Authorization learn mode (P1.3) ──────────────────────────────
        private async void BtnLearnMode_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            bool currentlyOn = lbAgents.SelectedItem is AgentViewModel vm && vm.LearnMode;
            bool turnOn = !currentlyOn;
            await ExecuteActionAsync(
                () => _server.LearnModeAsync(_selectedMachineId, turnOn),
                turnOn
                    ? $"Learn mode policy saved for {_selectedMachineId}; awaiting endpoint verification. It fails closed if the server disconnects."
                    : $"Learn mode disabled for {_selectedMachineId}; awaiting endpoint verification.",
                turnOn ? StatusKind.Warning : StatusKind.Success);
        }

        // Preset timed unlock buttons (Tag contains minutes as string)
        private async void BtnPresetUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (sender is not Button b || !int.TryParse(b.Tag?.ToString(), out int mins)) return;

            string label = mins >= 60 ? $"{mins / 60}h" : $"{mins}min";
            await ExecuteActionAsync(
                () => _server.UnlockTimedAsync(_selectedMachineId, mins),
                $"Timed unlock policy saved for {_selectedMachineId} ({label}); awaiting endpoint verification.");
        }

        // Custom timed unlock
        private async void BtnTimedUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (!int.TryParse(txtMinutes.Text, out int mins) || mins is <= 0 or > 10080)
            {
                SetStatus("Enter 1 to 10080 minutes for temporary access (up to 7 days).", StatusKind.Warning);
                return;
            }
            await ExecuteActionAsync(
                () => _server.UnlockTimedAsync(_selectedMachineId, mins),
                $"Timed unlock policy saved for {_selectedMachineId} ({mins} minutes); awaiting endpoint verification.");
        }

        // ── Global lock / unlock ──────────────────────────────────────────────
        private async void BtnLockAll_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Save a lock policy for ALL known machines?\n\nUnapproved removable storage and portable devices will be blocked when each endpoint applies the policy.",
                "Confirm — Lock All",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            await ExecuteActionAsync(
                _server.LockAllAsync,
                "Lock policy saved for all known machines. Offline machines will apply the policy when they reconnect.");
        }

        private async void BtnUnlockAll_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Save an unlock policy for ALL known machines?\n\nDevices managed by USB Guardian will be restored when each endpoint applies the policy.",
                "Confirm — Unlock All",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            await ExecuteActionAsync(
                _server.UnlockAllAsync,
                "Unlock policy saved for all known machines; awaiting endpoint verification. Offline machines will apply it when they reconnect.");
        }

        // ── Granular removable/MTP device control ─────────────────────────────
        private async void BtnAllowDevice_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine() || _deviceListMachineId != _selectedMachineId) return;
            if (sender is Button b && b.Tag is string id)
            {
                if (await ExecuteActionAsync(
                    () => _server.AllowAsync(_selectedMachineId, id),
                    $"Allow policy saved for {id}; awaiting endpoint verification.",
                    refreshAgents: false))
                {
                    await TryRefreshDevicesAsync();
                }
            }
        }

        private async void BtnBlockDevice_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine() || _deviceListMachineId != _selectedMachineId) return;
            if (sender is Button b && b.Tag is string id)
            {
                if (await ExecuteActionAsync(
                    () => _server.BlockAsync(_selectedMachineId, id),
                    $"Block policy saved for {id}; awaiting endpoint verification.",
                    refreshAgents: false))
                {
                    await TryRefreshDevicesAsync();
                }
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        // ── Machine groups (P1.4) ─────────────────────────────────────────────
        private async void BtnSetGroup_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            string name = txtGroup.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) { SetStatus("Enter a group name.", StatusKind.Warning); return; }
            await ExecuteActionAsync(
                () => _server.SetGroupAsync(_selectedMachineId, name),
                $"{_selectedMachineId} assigned to group '{name}'.");
        }

        private string SelectedGroup() =>
            lbAgents.SelectedItem is AgentViewModel vm ? vm.Group : txtGroup.Text.Trim();

        private async void BtnGroupLock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            string g = SelectedGroup();
            await ExecuteActionAsync(
                () => _server.GroupLockAsync(g),
                $"Lock policy saved for all known machines in group '{g}'.");
        }

        private async void BtnGroupUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            string g = SelectedGroup();
            var confirm = MessageBox.Show(
                $"Unlock USB on ALL machines in group '{g}'?",
                "Confirm — Unlock Group", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await ExecuteActionAsync(
                () => _server.GroupUnlockAsync(g),
                $"Unlock policy saved for group '{g}'; awaiting endpoint verification.");
        }

        // ── Device-class sectors (P2.1) ───────────────────────────────────────
        private async Task ToggleSectorAsync(string sector, bool current)
        {
            if (NoMachine()) return;
            bool block = !current;
            if (sector == "wifi" && block &&
                MessageBox.Show(
                    "Block Wi-Fi on this machine?\n\nIf the agent connects to the server over Wi-Fi, this will disconnect it and you won't be able to re-enable it remotely.",
                    "Confirm — Block Wi-Fi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await ExecuteActionAsync(
                () => _server.SetSectorAsync(_selectedMachineId, sector, block),
                $"Sector '{sector}' {(block ? "block" : "allow")} policy saved for {_selectedMachineId}; awaiting endpoint verification.");
        }

        private async void BtnSectorOptical_Click(object sender, RoutedEventArgs e) =>
            await ToggleSectorAsync("optical", (lbAgents.SelectedItem as AgentViewModel)?.OpticalBlocked ?? false);
        private async void BtnSectorBluetooth_Click(object sender, RoutedEventArgs e) =>
            await ToggleSectorAsync("bluetooth", (lbAgents.SelectedItem as AgentViewModel)?.BluetoothBlocked ?? false);
        private async void BtnSectorWifi_Click(object sender, RoutedEventArgs e) =>
            await ToggleSectorAsync("wifi", (lbAgents.SelectedItem as AgentViewModel)?.WifiBlocked ?? false);

        // ── BadUSB auto-block toggle (P2.2) ───────────────────────────────────
        private async void BtnBadUsb_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            bool current = (lbAgents.SelectedItem as AgentViewModel)?.BadUsbBlock ?? false;
            bool block = !current;
            await ExecuteActionAsync(
                () => _server.SetBadUsbAsync(_selectedMachineId, block),
                $"BadUSB auto-block {(block ? "enabled" : "disabled (detect-only)")} on {_selectedMachineId}.");
        }

        // ── Remote management (P2.5) ──────────────────────────────────────────
        private async void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            await ExecuteActionAsync(
                () => _server.ReloadAsync(_selectedMachineId),
                $"Reload sent to {_selectedMachineId}.",
                refreshAgents: false);
        }

        private async void BtnReboot_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (MessageBox.Show($"Reboot '{_selectedMachineId}'? The user gets a 20-second warning.",
                "Confirm — Reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ExecuteActionAsync(
                () => _server.RebootAsync(_selectedMachineId),
                $"Reboot sent to {_selectedMachineId}.",
                refreshAgents: false);
        }

        private async void BtnShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            if (MessageBox.Show($"Shut down '{_selectedMachineId}'? The user gets a 20-second warning.",
                "Confirm — Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ExecuteActionAsync(
                () => _server.ShutdownAsync(_selectedMachineId),
                $"Shutdown sent to {_selectedMachineId}.",
                refreshAgents: false);
        }

        // ── Event history (P1.1) ──────────────────────────────────────────────
        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            if (NoMachine()) return;
            string name = lbAgents.SelectedItem is AgentViewModel vm ? vm.MachineName : _selectedMachineId;
            new HistoryWindow(_server, _selectedMachineId, name) { Owner = this }.Show();
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private async Task<bool> ExecuteActionAsync(
            Func<Task<string>> action,
            string successMessage,
            StatusKind successKind = StatusKind.Success,
            bool refreshAgents = true)
        {
            if (!_isConnected || _actionInProgress) return false;
            _actionInProgress = true;
            SetInteractionEnabled(false);
            SetGlobalButtonsEnabled(false);
            SetMachineButtonsEnabled(false);
            SetStatus("Submitting command…");
            try
            {
                await action();
                SetStatus(successMessage, successKind);
                if (refreshAgents) await RefreshAsync(silent: true);
                return true;
            }
            catch (Exception ex)
            {
                HandleOperationError(ex);
                return false;
            }
            finally
            {
                _actionInProgress = false;
                SetInteractionEnabled(true);
                SetGlobalButtonsEnabled(_isConnected);
                SetMachineButtonsEnabled(lbAgents.SelectedItem is AgentViewModel,
                    (lbAgents.SelectedItem as AgentViewModel)?.Online ?? false);
            }
        }

        private void SetInteractionEnabled(bool enabled)
        {
            lbAgents.IsEnabled = txtEndpointSearch.IsEnabled = cmbEndpointFilter.IsEnabled = enabled;
            btnConnect.IsEnabled = txtServerUrl.IsEnabled = txtPassword.IsEnabled = enabled;
            btnChangePassword.IsEnabled = enabled;
            btnRefresh.IsEnabled = enabled && _isConnected;
            lvDevices.IsEnabled = enabled && _isConnected && _deviceListMachineId == _selectedMachineId;
        }

        private void HandleOperationError(Exception ex)
        {
            bool connectionLost =
                ex is System.Net.Http.HttpRequestException or TaskCanceledException or ServerConnectionException ||
                ex is ServerApiException
                {
                    StatusCode: System.Net.HttpStatusCode.Unauthorized
                        or System.Net.HttpStatusCode.Forbidden
                };
            if (connectionLost)
            {
                SetConnected(false);
                SetGlobalButtonsEnabled(false);
                SetMachineButtonsEnabled(false);
            }
            SetStatus($"Operation failed: {ex.Message}", StatusKind.Error);
        }

        private bool NoMachine()
        {
            if (!_isConnected)
            {
                SetStatus("Connect to the server before changing endpoint policy.", StatusKind.Warning);
                return true;
            }
            if (string.IsNullOrEmpty(_selectedMachineId))
            {
                SetStatus("Select an endpoint from the left panel first.", StatusKind.Warning);
                return true;
            }
            return false;
        }

        private void SetConnected(bool connected, string serverUrl = "")
        {
            _isConnected = connected;
            lbAgents.Opacity = connected ? 1 : 0.6;
            lbAgents.ToolTip = connected ? null : "Last known inventory. Connect to verify current endpoint status.";
            btnRefresh.IsEnabled = connected && !_actionInProgress;
            if (connected)
            {
                lblConnDot.Text        = "●";
                lblConnDot.Foreground  = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                lblConnStatus.Text     = "Connected";
                lblServerSummary.Text = $"  ·  {serverUrl}";
                bdConnectionStatus.ToolTip = serverUrl;
                lblConnStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                bdConnectionStatus.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xF5, 0xEC));
            }
            else
            {
                lblConnDot.Text        = "○";
                lblConnDot.Foreground  = new SolidColorBrush(Color.FromRgb(0x59, 0x68, 0x7E));
                lblConnStatus.Text     = "Disconnected";
                lblConnStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x59, 0x68, 0x7E));
                bdConnectionStatus.Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xED, 0xF4));
                bdConnectionStatus.ToolTip = "Connect to your USB Guardian server.";
                connectionPanel.IsExpanded = true;
                lblAutoRefresh.Text = _refreshTimer?.IsEnabled == true ? "Connection lost · Retrying every 5s" : "Connect to begin monitoring";
                lblTotal.Text = lblOnline.Text = lblOfflineCount.Text = lblAttention.Text = "—";
                _deviceRequestVersion++;
                _devices.Clear();
                _deviceListMachineId = string.Empty;
                lvDevices.ItemsSource = null;
                lvDevices.IsEnabled = false;
                if (!string.IsNullOrEmpty(_selectedMachineId))
                {
                    lblVerification.Text = "Server disconnected · Endpoint status is unverified";
                    lblDeviceCount.Text = "Live inventory unavailable";
                    pnlNoMachine.Visibility = pnlOffline.Visibility = Visibility.Collapsed;
                    pnlNoDevices.Visibility = Visibility.Visible;
                    lblDeviceEmptyTitle.Text = "Server connection unavailable";
                    lblDeviceEmptyDescription.Text = "Reconnect to load current device status and manage access.";
                }
                SetGlobalButtonsEnabled(false);
                SetMachineButtonsEnabled(false);
            }
            btnConnect.Content = connected ? "Reconnect" : "Connect";
        }

        private void SetGlobalButtonsEnabled(bool enabled)
        {
            enabled = enabled && _isConnected && !_actionInProgress;
            btnLockAll.IsEnabled   = enabled;
            btnUnlockAll.IsEnabled = enabled;
        }

        private void SetMachineButtonsEnabled(bool enabled, bool online = false)
        {
            enabled = enabled && _isConnected && !_actionInProgress;
            pnlPolicyContent.IsEnabled = pnlAdministrationContent.IsEnabled = enabled;
            btnLock.IsEnabled        = enabled;
            btnUnlock.IsEnabled      = enabled;
            btnLearnMode.IsEnabled   = enabled;
            btnHistory.IsEnabled     = enabled;
            btnSetGroup.IsEnabled    = enabled;
            btnGroupLock.IsEnabled   = enabled;
            btnGroupUnlock.IsEnabled = enabled;
            btnReload.IsEnabled      = enabled && online;
            btnReboot.IsEnabled      = enabled && online;
            btnShutdown.IsEnabled    = enabled && online;
            btnSecOptical.IsEnabled  = enabled;
            btnSecBluetooth.IsEnabled = enabled;
            btnSecWifi.IsEnabled     = enabled;
            btnBadUsb.IsEnabled      = enabled;
            btnPreset15.IsEnabled    = enabled;
            btnPreset30.IsEnabled    = enabled;
            btnPreset60.IsEnabled    = enabled;
            btnPreset240.IsEnabled   = enabled;
            btnPreset480.IsEnabled   = enabled;
            btnCustomUnlock.IsEnabled = enabled;
        }

        private enum StatusKind { Info, Success, Warning, Error }

        private void SetStatus(string msg, StatusKind kind = StatusKind.Info)
        {
            lblStatus.Text = msg;
            (bdStatus.Background, lblStatus.Foreground) = kind switch
            {
                StatusKind.Success => (new SolidColorBrush(Color.FromRgb(0xDF, 0xF6, 0xDD)),
                                       new SolidColorBrush(Color.FromRgb(0x07, 0x50, 0x07))),
                StatusKind.Error   => (new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9)),
                                       new SolidColorBrush(Color.FromRgb(0xA0, 0x00, 0x00))),
                StatusKind.Warning => (new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)),
                                       new SolidColorBrush(Color.FromRgb(0x60, 0x3D, 0x00))),
                _                  => (new SolidColorBrush(Color.FromRgb(0xEA, 0xF0, 0xFA)),
                                       new SolidColorBrush(Color.FromRgb(0x35, 0x45, 0x5D)))
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer?.Stop();
            _server.Dispose();
            base.OnClosed(e);
        }
    }
}
