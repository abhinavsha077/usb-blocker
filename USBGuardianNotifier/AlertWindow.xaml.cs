using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace USBGuardianNotifier
{
    public partial class AlertWindow : Window
    {
        private readonly DispatcherTimer _autoClose;
        private DateTime _shownAt;

        public AlertWindow(string device)
        {
            InitializeComponent();

            // Auto-dismiss after 12s so a legitimately-removed device doesn't leave the
            // alert stuck. (A future enhancement: hold until the device is physically gone
            // + optional group master password, matching the reference product.)
            _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _autoClose.Tick += (_, _) => Close();
            SetDevice(device);
        }

        public void SetDevice(string device)
        {
            lblDevice.Text = string.IsNullOrWhiteSpace(device) ? "Unauthorized USB device" : device;
            _shownAt = DateTime.UtcNow;   // refresh the dwell time on a repeated alert
            _autoClose.Stop();
            _autoClose.Start();
        }

        // Require the alert to be visible for at least 2s before a click/key can dismiss it,
        // so an in-flight keystroke-injection payload can't instantly close it.
        private bool DwellElapsed => (DateTime.UtcNow - _shownAt).TotalSeconds >= 2;

        private void Window_Dismiss(object sender, MouseButtonEventArgs e)
        {
            if (DwellElapsed) Close();
        }

        private void Window_Key(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && DwellElapsed) Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _autoClose.Stop();
            base.OnClosed(e);
        }
    }
}
