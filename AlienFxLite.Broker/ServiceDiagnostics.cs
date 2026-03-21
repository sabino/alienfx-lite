using System.Diagnostics;

namespace AlienFxLite.Broker;

internal sealed class ServiceDiagnostics
{
    private readonly object _sync = new();

    public ServiceDiagnostics()
    {
        RootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlienFxLite");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        StateFilePath = Path.Combine(RootDirectory, "state.json");
        ConfigFilePath = Path.Combine(RootDirectory, "service.json");
        LogFilePath = Path.Combine(LogsDirectory, "service.log");
        EnsureDirectories();
    }

    public string RootDirectory { get; }

    public string LogsDirectory { get; }

    public string StateFilePath { get; }

    public string ConfigFilePath { get; }

    public string LogFilePath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Fatal(string message, Exception? exception = null)
    {
        Write("FATAL", message, exception);
        WriteEventLog($"{message}{Environment.NewLine}{exception}", EventLogEntryType.Error);
    }

    public void WriteEventLog(string message, EventLogEntryType type)
    {
        try
        {
            const string source = "AlienFxLiteService";
            if (!EventLog.SourceExists(source))
            {
                EventLog.CreateEventSource(source, "Application");
            }

            EventLog.WriteEntry(source, message, type);
        }
        catch
        {
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        string line = $"[{DateTimeOffset.Now:O}] [{level}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
    }
}
