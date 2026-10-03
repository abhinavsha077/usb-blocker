using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace USBGuardianControl
{
    public partial class HistoryWindow : Window
    {
        private readonly ServerClient _server;
        private readonly string _machineId;
        private bool _loading;

        public HistoryWindow(ServerClient server, string machineId, string machineName)
        {
            InitializeComponent();
            _server = server;
            _machineId = machineId;
            lblTitle.Text = $"Event history · {machineName}";
            Loaded += async (_, _) => await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            btnRefreshHistory.IsEnabled = false;
            lblCount.Text = "Loading events…";
            try
            {
                var events = await _server.GetHistoryAsync(_machineId, 500);
                var vms = events.Select(e => new EventRow(e)).ToList();
                lvHistory.ItemsSource = vms;
                lblCount.Text = $"{vms.Count} event(s)";
                pnlEmpty.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                lblEmptyTitle.Text = "No events recorded";
                lblEmptyDescription.Text = "Device activity will appear here after the endpoint reports it.";
            }
            catch (Exception ex)
            {
                lvHistory.ItemsSource = null;
                lblCount.Text = "History unavailable";
                lblEmptyTitle.Text = "Unable to load event history";
                lblEmptyDescription.Text = ex.Message;
                pnlEmpty.Visibility = Visibility.Visible;
            }
            finally
            {
                _loading = false;
                btnRefreshHistory.IsEnabled = true;
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Row view-model: formats + colour-codes events (mirrors USB-Lock-RP's scheme).
        private class EventRow
        {
            private readonly UsbEventDto _e;
            public EventRow(UsbEventDto e) => _e = e;

            public string EventType => _e.EventType;
            public string TimeText  => _e.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            public string Detail    => _e.Detail;
            public string DeviceId => _e.DeviceId;
            public string DeviceText =>
                string.IsNullOrWhiteSpace(_e.DeviceName)
                    ? _e.DeviceId
                    : (string.IsNullOrWhiteSpace(_e.DeviceId) ? _e.DeviceName : $"{_e.DeviceName}");

            public string Color => _e.EventType switch
            {
                "Blocked"         => "#D13438",  // red
                "BlockFailed"     => "#D13438",
                "BlockedByAdmin"  => "#D13438",
                "BadUSBBlocked"   => "#D13438",
                "BadUSBBlockFailed" => "#D13438",
                "BadUSBRestoreFailed" => "#D13438",
                "Locked"          => "#D13438",
                "Connected"       => "#107C10",  // green
                "Authorized"      => "#107C10",
                "LearnCaptured"   => "#107C10",
                "BadUSBRestored"  => "#107C10",
                "Unlocked"        => "#0078D7",  // blue
                "LearnOn"         => "#B47B00",  // amber
                "LearnOff"        => "#8A8886",  // grey
                _                  => "#605E5C"
            };
        }
    }
}
