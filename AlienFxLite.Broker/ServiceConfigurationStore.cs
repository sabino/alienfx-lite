using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AlienFxLite.Broker;

internal sealed record ServiceConfiguration(string? AllowedUserSid);

internal sealed class ServiceConfigurationStore
{
    private readonly ServiceDiagnostics _diagnostics;

    public ServiceConfigurationStore(ServiceDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public ServiceConfiguration Load()
    {
        if (!File.Exists(_diagnostics.ConfigFilePath))
        {
            return new ServiceConfiguration(null);
        }

        try
        {
            string json = File.ReadAllText(_diagnostics.ConfigFilePath);
            return JsonSerializer.Deserialize<ServiceConfiguration>(json, AlienFxLite.Contracts.ServiceJson.Options)
                ?? new ServiceConfiguration(null);
        }
        catch (Exception ex)
        {
            _diagnostics.Error("Failed to load service configuration. Falling back to the default pipe ACL.", ex);
            return new ServiceConfiguration(null);
        }
    }

    public void Save(ServiceConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_diagnostics.ConfigFilePath)!);
        string json = JsonSerializer.Serialize(configuration, AlienFxLite.Contracts.ServiceJson.Options);
        File.WriteAllText(_diagnostics.ConfigFilePath, json);
    }

    public PipeSecurity CreatePipeSecurity(ServiceConfiguration configuration)
    {
        PipeSecurity security = new();

        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (!string.IsNullOrWhiteSpace(configuration.AllowedUserSid))
        {
            SecurityIdentifier user = new(configuration.AllowedUserSid);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        else
        {
            SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new PipeAccessRule(users, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            _diagnostics.Warn("No allowed user SID configured; falling back to Builtin Users pipe access.");
        }

        return security;
    }
}
