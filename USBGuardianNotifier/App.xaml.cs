using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace USBGuardianNotifier
{
    /// <summary>
    /// Endpoint alert notifier (P2.3). Runs in the interactive user session (launched at
    /// logon) — the agent Windows service runs in session 0 and cannot draw UI to the user.
    ///
    /// IPC: the agent writes an alert JSON to C:\ProgramData\USBGuardian\alerts\ (world-readable)
    /// whenever it blocks a device. This app watches that folder and shows a full-screen alert.
    /// </summary>
    public partial class App : Application
    {
        private static readonly string AlertDir = @"C:\ProgramData\USBGuardian\alerts";
        private FileSystemWatcher? _watcher;
        private DispatcherTimer? _watcherRetry;
        private AlertWindow? _current;
        private readonly BlockAlertInbox _inbox = new(AlertDir);
        private bool _polling;

        private void App_Startup(object sender, StartupEventArgs e)
        {
            _watcherRetry = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _watcherRetry.Tick += (_, _) => { TryStartWatcher(); PollInbox(); };
            _watcherRetry.Start();
            TryStartWatcher();
        }

        private void TryStartWatcher()
        {
            if (_watcher != null) return;

            try
            {
                Directory.CreateDirectory(AlertDir);
                var watcher = new FileSystemWatcher(AlertDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += OnAlertCreated;
                watcher.Renamed += OnAlertRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
                // Watch first, then scan: arrivals during setup cannot be lost.
                PollInbox();
            }
            catch
            {
                _watcher?.Dispose();
                _watcher = null;
                _watcherRetry?.Start();
            }
        }

        private void OnAlertCreated(object sender, FileSystemEventArgs args) =>
            Dispatcher.BeginInvoke(new Action(PollInbox));

        private void OnAlertRenamed(object sender, RenamedEventArgs args) =>
            Dispatcher.BeginInvoke(new Action(PollInbox));

        private void OnWatcherError(object sender, ErrorEventArgs args) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnAlertCreated;
                    _watcher.Renamed -= OnAlertRenamed;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                    _watcher = null;
                }
                TryStartWatcher();
            });

        private async void PollInbox()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                var pending = await Task.Run(() => _inbox.ReadPending(DateTime.UtcNow));
                if (Dispatcher.HasShutdownStarted) return;
                foreach (string device in pending) ShowAlert(device);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { _polling = false; }
        }

        private void ShowAlert(string device)
        {
            // One alert window at a time — refresh its text if already open.
            if (_current is { IsLoaded: true })
            {
                _current.SetDevice(device);
                return;
            }
            _current = new AlertWindow(device);
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _watcherRetry?.Stop();
            _watcher?.Dispose();
            base.OnExit(e);
        }

    }
}
