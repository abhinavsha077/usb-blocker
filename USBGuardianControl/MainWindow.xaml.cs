using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace USBGuardianControl
{
    public class UsbDeviceViewModel
    {
        public string DeviceID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsAllowed { get; set; }

        public string PolicyText => IsAllowed ? "ALLOWED" : "BLOCKED";
        public string PolicyColor => IsAllowed ? "#107C10" : "#D13438";
        
        public Visibility ShowAllow => IsAllowed ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ShowBlock => IsAllowed ? Visibility.Visible : Visibility.Collapsed;
    }

    public partial class MainWindow : Window
    {
        private const string PipeName = "USBGuardianControlPipe";
        private DispatcherTimer _autoRefreshTimer;
        private bool _isRefreshing = false;

        public MainWindow()
        {
            InitializeComponent();
            
            _autoRefreshTimer = new DispatcherTimer();
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(3);
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            _autoRefreshTimer.Start();
        }

        private async void AutoRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (!_isRefreshing && !string.IsNullOrEmpty(txtPassword.Password))
            {
                await RefreshDevicesAndStatusAsync(silent: true);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAndStatusAsync(silent: false);
        }

        private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("UNLOCK", "");
            await RefreshDevicesAndStatusAsync(silent: false);
        }

        private async void BtnLock_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("LOCK", "");
            await RefreshDevicesAndStatusAsync(silent: false);
        }

        private async void BtnTimedUnlock_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandAsync("UNLOCK", txtMinutes.Text.Trim());
            await RefreshDevicesAndStatusAsync(silent: false);
        }

        private async void BtnAllowDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string deviceId)
            {
                await SendCommandAsync("ALLOW", deviceId);
                await RefreshDevicesAndStatusAsync(silent: false);
            }
        }

        private async void BtnBlockDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string deviceId)
            {
                await SendCommandAsync("BLOCK", deviceId);
                await RefreshDevicesAndStatusAsync(silent: false);
            }
        }

        private void BtnOpenChangePassword_Click(object sender, RoutedEventArgs e)
        {
            string currentPassword = txtPassword.Password;
            if (string.IsNullOrEmpty(currentPassword))
            {
                lblStatus.Text = "Please enter your current admin password first.";
                return;
            }

            var dialog = new ChangePasswordWindow(currentPassword) { Owner = this };
            dialog.ShowDialog();
        }


        private async Task RefreshDevicesAndStatusAsync(bool silent = false)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            
            try
            {
                string masterStatus = "Connected.";
                
                string statusResponse = await SendCommandAsync("STATUS", "", silent);
                if (statusResponse != null && statusResponse.StartsWith("SUCCESS:"))
                {
                    masterStatus = statusResponse.Substring(8);
                }

                string listResponse = await SendCommandAsync("LIST", "", silent);
                if (listResponse != null && listResponse.StartsWith("SUCCESS:"))
                {
                    string json = listResponse.Substring(8);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var devices = JsonSerializer.Deserialize<List<UsbDeviceViewModel>>(json, options);
                    
                    if (devices != null)
                    {
                        lvDevices.ItemsSource = devices;
                    }
                }
                
                if (!silent)
                {
                    lblStatus.Text = masterStatus;
                }
            }
            catch (Exception ex)
            {
                if (!silent) lblStatus.Text = "Failed to update: " + ex.Message;
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async Task<string> SendCommandAsync(string command, string args, bool silent = false)
        {
            string password = txtPassword.Password;
            if (string.IsNullOrEmpty(password))
            {
                if (!silent) lblStatus.Text = "Please enter the admin password.";
                return "";
            }

            if (!silent) lblStatus.Text = $"Sending {command}...";
            
            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await pipeClient.ConnectAsync(2000);

                    using (var reader = new StreamReader(pipeClient))
                    using (var writer = new StreamWriter(pipeClient) { AutoFlush = true })
                    {
                        string request = $"{password}:{command}:{args}";
                        await writer.WriteLineAsync(request);

                        string response = await reader.ReadLineAsync();
                        
                        if (string.IsNullOrEmpty(response)) {
                            lblStatus.Text = "No response from service.";
                            return "";
                        }
                        
                        if (response.StartsWith("ERROR:"))
                        {
                            lblStatus.Text = response;
                        }

                        return response;
                    }
                }
            }
            catch (TimeoutException)
            {
                lblStatus.Text = "Service not running, inaccessible, or busy.";
            }
            catch (UnauthorizedAccessException)
            {
                lblStatus.Text = "Access denied to Named Pipe.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Error: {ex.Message}";
            }

            return "";
        }
    }
}
