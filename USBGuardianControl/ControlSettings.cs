using System.IO;
using System.Text.Json;

namespace USBGuardianControl
{
    internal class ControlSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "USBGuardian", "control_settings.json");

        public string ServerUrl { get; set; } = "http://localhost:5050";

        public static ControlSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<ControlSettings>(File.ReadAllText(FilePath)) ?? new();
            }
            catch { }
            return new();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string tempPath = FilePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
