using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace USBGuardianService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "USB Guardian Service";
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton<ConfigManager>();
                    services.AddSingleton<UsbPolicyManager>();
                    
                    services.AddHostedService<PipeServer>();
                    services.AddHostedService<Worker>();
                });
    }
}
