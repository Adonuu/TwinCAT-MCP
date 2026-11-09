namespace TwinCatMcp.Automation.Models;

public sealed record TwinCatProjectInfo(string Name, string Path, bool Selected);
public sealed record XaeTreeItemInfo(string Name, string TreePath, int ItemType, int ItemSubType);
public sealed record DeviceAdapterInfo(string? Description, string? DeviceName, string? Mac);
public sealed record TaskConfiguration(long CycleTimeUs, int Priority, string? CpuAffinity);
public sealed record CpuCoreConfiguration(int CoreId, int BaseTimeUs);
public sealed record ConfigurationOperationResult(bool Applied, bool Succeeded, bool DryRun, string SafetyReason, object? Value, string? Error, bool MayHaveApplied = false);
