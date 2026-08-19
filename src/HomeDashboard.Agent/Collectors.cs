using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public interface IAgentCollector
{
    AgentSnapshot Collect();
}

public sealed class AgentCollector(
    IOptions<AgentOptions> options,
    ISystemSnapshotCollector systemCollector,
    IWindowsServiceSnapshotCollector serviceCollector) : IAgentCollector
{
    public AgentSnapshot Collect()
        => new(options.Value.AgentId, DateTimeOffset.UtcNow, systemCollector.Collect(), serviceCollector.Collect());
}

public interface ISystemSnapshotCollector
{
    SystemStats Collect();
}

public sealed class SystemSnapshotCollector : ISystemSnapshotCollector
{
    public SystemStats Collect()
    {
        var disks = DriveInfo
            .GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DiskStats(drive.Name, drive.TotalSize, drive.AvailableFreeSpace))
            .ToArray();

        return new SystemStats(
            Environment.MachineName,
            0,
            GetProcessMemoryPercent(),
            disks,
            DateTimeOffset.UtcNow);
    }

    private static double GetProcessMemoryPercent()
    {
        var memory = GC.GetGCMemoryInfo();
        if (memory.TotalAvailableMemoryBytes <= 0)
        {
            return 0;
        }

        return Math.Round(Math.Clamp((double)Process.GetCurrentProcess().WorkingSet64 / memory.TotalAvailableMemoryBytes * 100, 0, 100), 1);
    }
}

public interface IWindowsServiceSnapshotCollector
{
    IReadOnlyList<ServiceCard> Collect();
}

public sealed class WindowsServiceSnapshotCollector(IOptions<AgentOptions> options) : IWindowsServiceSnapshotCollector
{
    public IReadOnlyList<ServiceCard> Collect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return options.Value.WindowsServices
                .Select(service => ToUnavailableCard(service, "Windows service monitoring is only available on Windows."))
                .ToArray();
        }

        return CollectWindowsServices(options.Value.WindowsServices);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ServiceCard> CollectWindowsServices(IReadOnlyList<WindowsServiceMonitor> configuredServices)
    {
        var servicesByName = ServiceController
            .GetServices()
            .ToDictionary(service => service.ServiceName, StringComparer.OrdinalIgnoreCase);

        return configuredServices
            .Select(service => servicesByName.TryGetValue(service.ServiceName, out var controller)
                ? ToServiceCard(service, controller)
                : ToUnavailableCard(service, "Windows service was not found."))
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static ServiceCard ToServiceCard(WindowsServiceMonitor service, ServiceController controller)
        => new(
            service.Id,
            service.DisplayName,
            service.ServiceName,
            null,
            ToDashboardStatus(controller.Status),
            service.RestartEnabled,
            DateTimeOffset.UtcNow,
            $"Windows service status is {controller.Status}.");

    private static ServiceCard ToUnavailableCard(WindowsServiceMonitor service, string message)
        => new(
            service.Id,
            service.DisplayName,
            service.ServiceName,
            null,
            ServiceStatus.Unknown,
            service.RestartEnabled,
            DateTimeOffset.UtcNow,
            message);

    [SupportedOSPlatform("windows")]
    private static ServiceStatus ToDashboardStatus(ServiceControllerStatus status)
        => status switch
        {
            ServiceControllerStatus.Running => ServiceStatus.Online,
            ServiceControllerStatus.Paused or ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending => ServiceStatus.Degraded,
            ServiceControllerStatus.Stopped => ServiceStatus.Offline,
            _ => ServiceStatus.Unknown
        };
}
