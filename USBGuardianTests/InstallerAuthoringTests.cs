using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace USBGuardianTests;

public class InstallerAuthoringTests
{
    private static readonly XNamespace Wix =
        "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace WixUtil =
        "http://wixtoolset.org/schemas/v4/wxs/util";

    [Fact]
    public void InstallerProjectsUsePinnedModernWixSdk()
    {
        foreach (string project in InstallerFiles("*.wixproj"))
        {
            var document = XDocument.Load(project);
            Assert.Equal("WixToolset.Sdk/4.0.6",
                document.Root?.Attribute("Sdk")?.Value);
        }
    }

    [Fact]
    public void PackagesHaveUniqueUpgradeCodesAndMajorUpgradeRules()
    {
        var packages = ProductFiles()
            .Select(path => XDocument.Load(path).Root?.Element(Wix + "Package"))
            .ToList();

        Assert.Equal(3, packages.Count);
        Assert.All(packages, package =>
        {
            Assert.NotNull(package);
            Assert.Equal("perMachine", package!.Attribute("Scope")?.Value);
            Assert.NotNull(package.Element(Wix + "MajorUpgrade"));
        });
        Assert.Equal(3, packages
            .Select(package => package!.Attribute("UpgradeCode")?.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());
    }

    [Fact]
    public void EndpointPackageRequiresHiddenTokenAndInstallsCurrentServices()
    {
        var document = LoadProduct("Endpoint");

        AssertSecureHiddenProperty(document, "AGENTTOKEN");
        AssertService(document, "USB Guardian Agent");
        Assert.Contains(document.Descendants(Wix + "File"),
            file => file.Attribute("Source")?.Value.EndsWith(
                "USBGuardianAgent.exe", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(Wix + "File"),
            file => file.Attribute("Source")?.Value.EndsWith(
                "USBGuardianNotifier.exe", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(Wix + "RegistryValue"),
            value => value.Attribute("Name")?.Value == "USBGuardianNotifier");
        AssertProtectedRegistryConfiguration(document);
    }

    [Fact]
    public void EndpointRejectsWindowsWithoutPnputilEnableDisableCommands()
    {
        var document = LoadProduct("Endpoint");
        var build = Assert.Single(document.Descendants(Wix + "Property"),
            property => property.Attribute("Id")?.Value == "WINDOWSBUILDNUMBER");
        Assert.Contains(build.Descendants(Wix + "RegistrySearch"),
            search => search.Attribute("Name")?.Value == "CurrentBuildNumber");
        Assert.Contains(document.Descendants(Wix + "Launch"),
            condition => condition.Attribute("Condition")?.Value ==
                "Installed OR (WINDOWSBUILDNUMBER >= 19041)");
    }

    [Fact]
    public void EndpointUninstallRestoresOnlyAgentManagedDevices()
    {
        var document = LoadProduct("Endpoint");
        var action = Assert.Single(
            document.Descendants(Wix + "CustomAction"),
            item => item.Attribute("Id")?.Value == "RestoreManagedDevices");

        Assert.Equal("AgentExe", action.Attribute("FileRef")?.Value);
        Assert.Equal(
            "--restore-managed-devices",
            action.Attribute("ExeCommand")?.Value);
        Assert.Equal("deferred", action.Attribute("Execute")?.Value);
        Assert.Equal("no", action.Attribute("Impersonate")?.Value);

        var scheduled = Assert.Single(
            document.Descendants(Wix + "Custom"),
            item => item.Attribute("Action")?.Value == "RestoreManagedDevices");
        Assert.Equal("StopServices", scheduled.Attribute("After")?.Value);
        string condition = scheduled.Attribute("Condition")?.Value ?? string.Empty;
        Assert.Contains("REMOVE", condition, StringComparison.Ordinal);
        Assert.Contains(
            "NOT UPGRADINGPRODUCTCODE",
            condition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServerPackageRequiresHiddenTokenAndConfiguresRecoveryAndFirewall()
    {
        var document = LoadProduct("Server");

        AssertSecureHiddenProperty(document, "AGENTTOKEN");
        AssertService(document, "USB Guardian Server");
        Assert.Contains(document.Descendants(),
            element => element.Name.LocalName == "FirewallException" &&
                element.Attribute("Port")?.Value == "5050");
        AssertProtectedRegistryConfiguration(document);
    }

    [Fact]
    public void ControlPackageContainsDashboardWithoutAService()
    {
        var document = LoadProduct("Control");

        Assert.Empty(document.Descendants(Wix + "ServiceInstall"));
        Assert.Contains(document.Descendants(Wix + "File"),
            file => file.Attribute("Source")?.Value.EndsWith(
                "USBGuardianControl.exe", StringComparison.Ordinal) == true);
        Assert.Single(document.Descendants(Wix + "Shortcut"));
    }

    [Fact]
    public void InstallerAuthoringHasNoLegacyServiceReference()
    {
        foreach (string file in ProductFiles())
        {
            Assert.DoesNotContain(
                "USBGuardianService",
                File.ReadAllText(file),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertSecureHiddenProperty(
        XDocument document,
        string propertyId)
    {
        var property = Assert.Single(
            document.Descendants(Wix + "Property"),
            item => item.Attribute("Id")?.Value == propertyId);
        Assert.Equal("yes", property.Attribute("Secure")?.Value);
        Assert.Equal("yes", property.Attribute("Hidden")?.Value);
    }

    private static void AssertService(XDocument document, string serviceName)
    {
        var service = Assert.Single(
            document.Descendants(Wix + "ServiceInstall"),
            item => item.Attribute("Name")?.Value == serviceName);
        Assert.Equal("LocalSystem", service.Attribute("Account")?.Value);
        Assert.Equal("auto", service.Attribute("Start")?.Value);

        var recovery = Assert.Single(service.Elements(WixUtil + "ServiceConfig"));
        Assert.Equal("restart",
            recovery.Attribute("FirstFailureActionType")?.Value);
        Assert.Equal("restart",
            recovery.Attribute("SecondFailureActionType")?.Value);
        Assert.Equal("none",
            recovery.Attribute("ThirdFailureActionType")?.Value);
        Assert.Equal("5",
            recovery.Attribute("RestartServiceDelayInSeconds")?.Value);
    }

    private static void AssertProtectedRegistryConfiguration(XDocument document)
    {
        var permission = Assert.Single(document.Descendants(Wix + "PermissionEx"));
        string sddl = permission.Attribute("Sddl")?.Value ?? string.Empty;
        Assert.Contains(";;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains(";;;BA)", sddl, StringComparison.Ordinal);

        var component = permission.Ancestors(Wix + "Component").Single();
        Assert.Null(component.Attribute("Permanent"));
        Assert.Null(component.Attribute("NeverOverwrite"));

        var registryKey = permission.Parent;
        Assert.NotNull(registryKey);
        Assert.Equal("yes",
            registryKey!.Attribute("ForceCreateOnInstall")?.Value);

        Assert.Contains(document.Descendants(Wix + "SetProperty"),
            setting =>
                setting.Attribute("Id")?.Value is "AGENTTOKEN" or "SERVERURL" &&
                setting.Attribute("After")?.Value == "AppSearch");
    }

    private static XDocument LoadProduct(string role) =>
        XDocument.Load(Path.Combine(
            RepositoryRoot(),
            "USBGuardianInstaller",
            role,
            "Product.wxs"));

    private static IEnumerable<string> ProductFiles() =>
        InstallerFiles("Product.wxs");

    private static IEnumerable<string> InstallerFiles(string pattern) =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "USBGuardianInstaller"),
            pattern,
            SearchOption.AllDirectories);

    private static string RepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        string? testProjectDirectory = Path.GetDirectoryName(sourceFile);
        return Directory.GetParent(testProjectDirectory
            ?? throw new DirectoryNotFoundException(
                "Could not locate the USB Guardian test project."))?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the USB Guardian repository root.");
    }
}
