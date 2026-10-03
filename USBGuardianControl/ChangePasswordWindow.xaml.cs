using System.Threading.Tasks;
using System.Windows;

namespace USBGuardianControl
{
    public partial class ChangePasswordWindow : Window
    {
        private readonly ServerClient _server;
        private readonly string _currentPassword;
        public string NewPassword { get; private set; } = string.Empty;

        public ChangePasswordWindow(ServerClient server, string currentPassword)
        {
            InitializeComponent();
            _server = server;
            _currentPassword = currentPassword;
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            string newPwd     = txtNewPwd.Password;
            string confirmPwd = txtConfirmPwd.Password;

            if (string.IsNullOrWhiteSpace(newPwd) || newPwd.Length < 8)
            {
                lblError.Text = "New password must contain at least 8 characters.";
                return;
            }
            if (newPwd.Length > 128)
            {
                lblError.Text = "New password cannot exceed 128 characters.";
                return;
            }
            if (string.Equals(newPwd, "admin", System.StringComparison.OrdinalIgnoreCase))
            {
                lblError.Text = "Choose a password other than the shipped default.";
                return;
            }
            if (newPwd != confirmPwd)
            {
                lblError.Text = "Passwords do not match.";
                return;
            }

            lblError.Text = "Sending...";
            btnConfirm.IsEnabled = btnCancel.IsEnabled = false;

            try
            {
                string response = await _server.ChangePasswordAsync(_currentPassword, newPwd);

                if (response == "Password changed.")
                {
                    NewPassword = newPwd;
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
                    lblError.Text = response ?? "Unknown error.";
                }
            }
            catch (System.Exception ex)
            {
                lblError.Text = $"ERROR: {ex.Message}";
            }
            finally
            {
                btnConfirm.IsEnabled = btnCancel.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
