using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Net.NetworkInformation;
using HomeDashboard.Contracts;
using Microsoft.Win32;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public interface IAgentCollector
{
    AgentSnapshot Collect();
}

public sealed class AgentCollector(
    IOptionsMonitor<AgentOptions> options,
    ISystemSnapshotCollector systemCollector,
    IWindowsServiceSnapshotCollector serviceCollector) : IAgentCollector
{
    public AgentSnapshot Collect()
        => new(options.CurrentValue.AgentId, DateTimeOffset.UtcNow, systemCollector.Collect(), serviceCollector.Collect(), options.CurrentValue.MachineActionsEnabled);
}

public interface ISystemSnapshotCollector
{
    SystemStats Collect();
}

public sealed class SystemSnapshotCollector(IOptionsMonitor<AgentOptions> options) : ISystemSnapshotCollector
{
    private readonly object detailLock = new();
    private IReadOnlyList<DiskStats> cachedDisks = [];
    private IReadOnlyList<ProcessStats> cachedTopProcesses = [];
    private bool cachedPendingReboot;
    private DateTimeOffset detailsExpireAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastSampledAt = DateTimeOffset.UtcNow;
    private TimeSpan lastProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly PerformanceCounter? cpuCounter = CreateCpuCounter();
    private DateTimeOffset lastNetworkSampledAt = DateTimeOffset.UtcNow;
    private long lastNetworkReceived = ReadNetworkBytes().Received;
    private long lastNetworkSent = ReadNetworkBytes().Sent;

    public SystemStats Collect()
    {
        var details = GetHostDetails();

        var network = GetNetworkRates();
        return new SystemStats(
            Environment.MachineName,
            (OperatingSystem.IsWindows() ? GetHostCpuPercent() : null) ?? GetProcessCpuPercent(),
            GetMemoryUsedPercent(GetProcessMemoryPercent()),
            details.Disks,
            DateTimeOffset.UtcNow,
            Environment.TickCount64 / 1000,
            Environment.OSVersion.VersionString,
            details.PendingReboot,
            network.Received,
            network.Sent,
            details.TopProcesses);
    }

    private (IReadOnlyList<DiskStats> Disks, IReadOnlyList<ProcessStats> TopProcesses, bool PendingReboot) GetHostDetails()
    {
        lock (detailLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= detailsExpireAt)
            {
                cachedDisks = DriveInfo.GetDrives().Where(drive => drive.IsReady)
                    .Select(drive => new DiskStats(drive.Name, drive.TotalSize, drive.AvailableFreeSpace)).ToArray();
                cachedTopProcesses = GetTopProcesses();
                cachedPendingReboot = IsPendingReboot();
                var interval = options.CurrentValue.HostDetailRefreshInterval;
                detailsExpireAt = now.Add(interval < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : interval);
            }

            return (cachedDisks, cachedTopProcesses, cachedPendingReboot);
        }
    }

    private (long Received, long Sent) GetNetworkRates()
    {
        var now = DateTimeOffset.UtcNow;
        var totals = ReadNetworkBytes();
        var elapsed = Math.Max((now - lastNetworkSampledAt).TotalSeconds, 0.1);
        var rates = (
            Math.Max(0, (long)((totals.Received - lastNetworkReceived) / elapsed)),
            Math.Max(0, (long)((totals.Sent - lastNetworkSent) / elapsed)));
        lastNetworkSampledAt = now;
        lastNetworkReceived = totals.Received;
        lastNetworkSent = totals.Sent;
        return rates;
    }

    private static (long Received, long Sent) ReadNetworkBytes()
    {
        try
        {
            var stats = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(item => item.GetIPv4Statistics())
                .ToArray();
            return (stats.Sum(item => item.BytesReceived), stats.Sum(item => item.BytesSent));
        }
        catch (NetworkInformationException)
        {
            return (0, 0);
        }
    }

    private static IReadOnlyList<ProcessStats> GetTopProcesses()
    {
        var largest = new PriorityQueue<ProcessStats, long>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var workingSet = process.WorkingSet64;
                if (largest.Count < 8 || largest.TryPeek(out _, out var smallest) && workingSet > smallest)
                {
                    if (largest.Count == 8)
                    {
                        largest.Dequeue();
                    }
                    largest.Enqueue(new ProcessStats(process.Id, process.ProcessName, workingSet, process.TotalProcessorTime), workingSet);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return largest.UnorderedItems.Select(item => item.Element).OrderByDescending(item => item.WorkingSetBytes).ToArray();
    }

    private static bool IsPendingReboot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var windowsUpdate = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var sessionManager = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return windowsUpdate is not null || sessionManager?.GetValue("PendingFileRenameOperations") is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private double? GetHostCpuPercent()
    {
        if (cpuCounter is null)
        {
            return null;
        }

        try
        {
            return Math.Round(Math.Clamp(cpuCounter.NextValue(), 0, 100), 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private double GetProcessCpuPercent()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        var processorTime = process.TotalProcessorTime;
        var elapsed = (now - lastSampledAt).TotalMilliseconds;
        var used = (processorTime - lastProcessorTime).TotalMilliseconds;
        lastSampledAt = now;
        lastProcessorTime = processorTime;

        if (elapsed <= 0)
        {
            return 0;
        }

        return Math.Round(Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100), 1);
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

    private static double GetMemoryUsedPercent(double fallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fallback;
        }

        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status) ? status.MemoryLoad : fallback;
    }

    private static PerformanceCounter? CreateCpuCounter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            counter.NextValue();
            return counter;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

public interface IWindowsServiceSnapshotCollector
{
    IReadOnlyList<ServiceCard> Collect();
}

public sealed class WindowsServiceSnapshotCollector(IOptionsMonitor<AgentOptions> options) : IWindowsServiceSnapshotCollector
{
    public IReadOnlyList<ServiceCard> Collect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return options.CurrentValue.WindowsServices
                .Select(service => ToUnavailableCard(service, "Windows service monitoring is only available on Windows."))
                .ToArray();
        }

        return CollectWindowsServices(options.CurrentValue.WindowsServices);
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
            ServiceKind.Generic,
            service.ServiceName,
            null,
            ToDashboardStatus(controller.Status),
            service.RestartEnabled,
            DateTimeOffset.UtcNow,
            $"Windows service status is {controller.Status}.",
            [new ServiceMetric("Windows", controller.Status.ToString())]);

    private static ServiceCard ToUnavailableCard(WindowsServiceMonitor service, string message)
        => new(
            service.Id,
            service.DisplayName,
            ServiceKind.Generic,
            service.ServiceName,
            null,
            ServiceStatus.Unknown,
            service.RestartEnabled,
            DateTimeOffset.UtcNow,
            message,
            []);

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

