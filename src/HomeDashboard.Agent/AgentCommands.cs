using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Diagnostics;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public interface IAgentCommandExecutor
{
    AgentCommandCompletion Execute(AgentCommand command);
}

public sealed class AgentCommandExecutor(IOptionsMonitor<AgentOptions> options) : IAgentCommandExecutor
{
    public AgentCommandCompletion Execute(AgentCommand command)
    {
        if (command.Kind != AgentCommandKind.RestartService)
        {
            if (!options.CurrentValue.MachineActionsEnabled)
            {
                return new AgentCommandCompletion(false, "Machine actions are disabled on this agent.");
            }
            if (!OperatingSystem.IsWindows())
            {
                return new AgentCommandCompletion(false, "Machine actions are only available on Windows.");
            }
            return ExecuteMachineAction(command.Kind);
        }

        var configured = options.CurrentValue.WindowsServices.FirstOrDefault(service =>
            service.Id.Equals(command.ServiceId, StringComparison.OrdinalIgnoreCase));
        if (configured is null)
        {
            return new AgentCommandCompletion(false, "Service is not configured on this agent.");
        }

        if (!configured.RestartEnabled)
        {
            return new AgentCommandCompletion(false, "Service is not enabled for restart on this agent.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new AgentCommandCompletion(false, "Windows service restart is only available on Windows.");
        }

        return RestartWindowsService(configured);
    }

    [SupportedOSPlatform("windows")]
    private static AgentCommandCompletion ExecuteMachineAction(AgentCommandKind kind)
    {
        var arguments = kind switch
        {
            AgentCommandKind.LockComputer => "user32.dll,LockWorkStation",
            AgentCommandKind.SleepComputer => "powrprof.dll,SetSuspendState 0,1,0",
            AgentCommandKind.RestartComputer => "/r /t 0 /d p:0:0 /c \"HomeDashboard approved restart\"",
            AgentCommandKind.ShutdownComputer => "/s /t 0 /d p:0:0 /c \"HomeDashboard approved shutdown\"",
            _ => null
        };
        if (arguments is null) return new AgentCommandCompletion(false, $"Unsupported command kind {kind}.");
        var executable = kind is AgentCommandKind.LockComputer or AgentCommandKind.SleepComputer ? "rundll32.exe" : "shutdown.exe";
        try
        {
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true });
            return new AgentCommandCompletion(true, $"{kind} was started successfully.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AgentCommandCompletion(false, $"{kind} failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static AgentCommandCompletion RestartWindowsService(WindowsServiceMonitor configured)
    {
        try
        {
            using var controller = new ServiceController(configured.ServiceName);
            controller.Refresh();

            if (controller.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return new AgentCommandCompletion(true, $"{configured.DisplayName} restarted successfully.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return new AgentCommandCompletion(false, $"Restart failed: {ex.Message}");
        }
    }
}
