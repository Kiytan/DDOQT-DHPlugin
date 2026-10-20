using System;
using System.IO;
using log4net;

namespace QuestTracker;

/// <summary>
/// Logs to QuestTracker.log in the plugin folder, when the player has turned that on, as well as
/// to log4net. Dungeon Helper 5 doesn't write plugins' log4net output anywhere, so without this file
/// there is no way to see what the plugin did. Debug messages only go to log4net, to keep the file
/// readable.
/// </summary>
public class PluginLog
{
    // Once the log reaches this size it replaces QuestTracker.log.1 and starts again, so the two
    // together never take more than twice this
    private const long MaxFileBytes = 512 * 1024;

    private readonly ILog _log;
    private readonly object _fileLock = new();
    private string? _filePath;

    public PluginLog(Type type)
    {
        _log = LogManager.GetLogger(type);
    }

    /// <summary>
    /// Whether messages are written to QuestTracker.log
    /// </summary>
    public bool FileEnabled { get; set; }

    /// <summary>
    /// The folder QuestTracker.log is written to
    /// </summary>
    public void SetFolder(string folder)
    {
        lock (_fileLock)
        {
            _filePath = Path.Combine(folder, "QuestTracker.log");
        }
    }

    public void Debug(string message) => _log.Debug(message);

    public void Info(string message)
    {
        _log.Info(message);
        WriteToFile("INFO ", message, null);
    }

    public void Warn(string message)
    {
        _log.Warn(message);
        WriteToFile("WARN ", message, null);
    }

    public void Error(string message, Exception ex)
    {
        _log.Error(message, ex);
        WriteToFile("ERROR", message, ex);
    }

    private void WriteToFile(string level, string message, Exception? ex)
    {
        try
        {
            if (!FileEnabled) return;

            lock (_fileLock)
            {
                if (_filePath == null) return;

                if (File.Exists(_filePath) && new FileInfo(_filePath).Length > MaxFileBytes)
                    File.Move(_filePath, _filePath + ".1", overwrite: true);

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
                if (ex != null)
                    line += ex + Environment.NewLine;

                File.AppendAllText(_filePath, line);
            }
        }
        catch
        {
            // Logging must never break the plugin
        }
    }
}
