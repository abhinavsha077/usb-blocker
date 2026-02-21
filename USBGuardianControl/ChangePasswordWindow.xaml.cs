using System.IO.Pipes;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace USBGuardianControl
{
    public partial class ChangePasswordWindow : Window
    {
        private const string PipeName = "USBGuardianControlPipe";
        private readonly string _currentPassword;

        public ChangePasswordWindow(string currentPassword)
        {
            InitializeComponent();
            _currentPassword = currentPassword;
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            string newPwd     = txtNewPwd.Password;
            string confirmPwd = txtConfirmPwd.Password;

            if (string.IsNullOrWhiteSpace(newPwd))
            {
                lblError.Text = "New password cannot be empty.";
                return;
            }
            if (newPwd != confirmPwd)
            {
                lblError.Text = "Passwords do not match.";
                return;
            }

            lblError.Text = "Sending...";
            string response = await SendChangePasswordAsync(newPwd);

            if (response != null && response.StartsWith("SUCCESS:"))
            {
                MessageBox.Show(
                    "Password changed successfully!\nUse the new password next time you connect.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            else
            {
                lblError.Text = response ?? "No response from service.";
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async Task<string> SendChangePasswordAsync(string newPassword)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000);

                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };

                await writer.WriteLineAsync($"{_currentPassword}:CHANGEPASSWORD:{newPassword}");
                return await reader.ReadLineAsync();
            }
            catch (System.TimeoutException)
            {
                return "ERROR:Service not running or unreachable.";
            }
            catch (System.Exception ex)
            {
                return $"ERROR:{ex.Message}";
            }
        }
    }
}
