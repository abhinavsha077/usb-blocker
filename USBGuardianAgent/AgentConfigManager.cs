using System.Text.Json;

namespace USBGuardianAgent
{
    public class AgentConfig
    {
        public string ServerUrl  { get; set; } = "http://localhost:5050";
        public string MachineId  { get; set; } = Environment.MachineName;
        public string AgentToken { get; set; } = "default-token";
    }

    public class AgentConfigManager
    {
        public AgentConfig Config { get; private set; }

        public AgentConfigManager()
        {
            var path = @"C:\ProgramData\USBGuardian\agent_config.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path))
            {
                Config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path))
                         ?? new AgentConfig();
            }
            else
            {
                Config = new AgentConfig { MachineId = Environment.MachineName };
                File.WriteAllText(path, JsonSerializer.Serialize(Config,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
