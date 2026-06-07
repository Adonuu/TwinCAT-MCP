using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TwinCatMcp.Safety;

public sealed record AuditEntry(
    DateTimeOffset TimestampUtc,
    string Operation,
    string Subject,
    string Verdict,
    string Reason,
    string? Details);

/// <summary>
/// Append-only forensic record of every mutating operation a <see cref="SafetyGate"/> evaluated — allowed,
/// denied, or pending confirmation — independent of whatever the LLM's own conversation log retains. Each
/// entry is a single JSON line, so the file stays greppable and diffable as it grows.
///
/// Failures to write the log are logged but never thrown — an audit-trail outage must not be able to take
/// down (or be exploited to silently bypass) the safety gate it's observing.
/// </summary>
public sealed class OperationAuditLog
{
    private readonly string _logFilePath;
    private readonly ILogger<OperationAuditLog> _logger;
    private readonly object _writeLock = new();

    public OperationAuditLog(string logFilePath, ILogger<OperationAuditLog> logger)
    {
        _logFilePath = logFilePath;
        _logger = logger;

        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public string LogFilePath => _logFilePath;

    public void Record(string operation, string subject, SafetyDecision decision, string? details = null)
    {
        var entry = new AuditEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            Operation: operation,
            Subject: subject,
            Verdict: decision.Verdict.ToString(),
            Reason: decision.Reason,
            Details: details);

        var line = JsonSerializer.Serialize(entry);

        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to append to operation audit log at '{Path}'", _logFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to append to operation audit log at '{Path}'", _logFilePath);
        }
    }
}
