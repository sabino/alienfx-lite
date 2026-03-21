using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace AlienFxLite.Broker;

public static class ServiceInstaller
{
    public const string ServiceName = "AlienFxLiteService";
    public const string DisplayName = "AlienFx Lite Service";
    public const string Description = "AlienFx Lite privileged broker for Dell fan and lighting control.";

    public static void InstallOrUpdate(string binaryPath, string? allowedUserSid = null)
    {
        EnsureAdministrator();

        string resolvedBinaryPath = Path.GetFullPath(binaryPath);
        if (!File.Exists(resolvedBinaryPath))
        {
            throw new FileNotFoundException("AlienFx Lite binary not found.", resolvedBinaryPath);
        }

        allowedUserSid ??= WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(allowedUserSid))
        {
            throw new InvalidOperationException("Unable to determine the current Windows user SID.");
        }

        ServiceDiagnostics diagnostics = new();
        ServiceConfigurationStore configurationStore = new(diagnostics);
        configurationStore.Save(new ServiceConfiguration(allowedUserSid));

        if (ServiceExists())
        {
            StopServiceIfNeeded();
            RunSc("delete", ServiceName);
            WaitForDeleted();
        }

        RunSc("create", ServiceName, "binPath=", $"\"{resolvedBinaryPath}\"", "DisplayName=", DisplayName, "start=", "auto");
        RunSc("description", ServiceName, Description);
        RunSc("config", ServiceName, "obj=", "LocalSystem", "start=", "delayed-auto");
        RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000");

        using ServiceController controller = new(ServiceName);
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Uninstall()
    {
        EnsureAdministrator();

        if (!ServiceExists())
        {
            return;
        }

        StopServiceIfNeeded();
        RunSc("delete", ServiceName);
        WaitForDeleted();
    }

    private static void EnsureAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Run this command from an elevated Administrator process.");
        }
    }

    private static bool ServiceExists()
    {
        try
        {
            using ServiceController controller = new(ServiceName);
            _ = controller.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void StopServiceIfNeeded()
    {
        using ServiceController controller = new(ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    private static void WaitForDeleted()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (!ServiceExists())
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new global::System.TimeoutException($"Timed out waiting for service '{ServiceName}' to be deleted.");
    }

    private static void RunSc(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("sc.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sc.exe.");
        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string renderedArguments = string.Join(" ", arguments.Select(RenderArgument));
            throw new InvalidOperationException(
                $"sc.exe {renderedArguments} failed with exit code {process.ExitCode}.{Environment.NewLine}{stdOut}{stdErr}".Trim());
        }
    }

    private static string RenderArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}
