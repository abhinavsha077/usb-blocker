namespace USBGuardianServer
{
    public class ServerConfigManager
    {
        private readonly string _filePath;
        private string _passwordHash;

        public ServerConfigManager(IConfiguration config)
        {
            _filePath = config["ConfigFilePath"] ?? @"C:\ProgramData\USBGuardian\server_config.json";
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            if (File.Exists(_filePath))
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(_filePath));
                _passwordHash = json?.PasswordHash ?? HashDefault();
            }
            else
            {
                _passwordHash = HashDefault();
                Save();
            }
        }

        public bool VerifyPassword(string plainPassword) =>
            BCrypt.Net.BCrypt.Verify(plainPassword, _passwordHash);

        public void ChangePassword(string newPassword)
        {
            _passwordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            Save();
        }

        private string HashDefault() => BCrypt.Net.BCrypt.HashPassword("admin");

        private void Save() =>
            File.WriteAllText(_filePath, System.Text.Json.JsonSerializer.Serialize(
                new ServerConfig { PasswordHash = _passwordHash },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        private class ServerConfig { public string PasswordHash { get; set; } = string.Empty; }
    }
}
