using System.Diagnostics;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public interface IAgentCollector
{
    AgentSnapshot Collect();
}

public sealed record AgentSnapshot(
    DateTimeOffset CapturedAt,
    SystemStats System,
    IReadOnlyList<ServiceCard> Services);

public sealed class AgentCollector(
    ISystemSnapshotCollector systemCollector,
    IWindowsServiceSnapshotCollector serviceCollector) : IAgentCollector
{
    public AgentSnapshot Collect()
        => new(DateTimeOffset.UtcNow, systemCollector.Collect(), serviceCollector.Collect());
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
        return options.Value.WindowsServices
            .Select(service => new ServiceCard(
                service.Id,
                service.DisplayName,
                service.ServiceName,
                null,
                ServiceStatus.Unknown,
                service.RestartEnabled,
                DateTimeOffset.UtcNow,
                OperatingSystem.IsWindows()
                    ? "Windows service lookup placeholder. Add ServiceController integration when command auth is ready."
                    : "Windows service monitoring is only available on Windows."))
            .ToArray();
    }
}
