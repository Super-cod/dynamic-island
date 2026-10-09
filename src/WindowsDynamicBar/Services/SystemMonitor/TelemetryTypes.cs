namespace WindowsDynamicBar.Services.SystemMonitor;

public sealed record CpuStats(
    double Utilization,
    string Name,
    double FrequencyMhz,
    int PhysicalCores,
    int LogicalThreads,
    bool Available);

public sealed record MemoryStats(
    double UsedPercent,
    long UsedBytes,
    long TotalBytes,
    bool Available);

public sealed record GpuAdapterStats(
    long Luid,
    string Name,
    double Utilization,
    long VramUsedBytes,
    long VramTotalBytes,
    bool VramAvailable);

/// <summary>A single process row for the top-process list (CPU share 0..100).</summary>
public sealed record ProcessSample(string Name, double CpuPercent);

public sealed record TelemetrySnapshot(
    CpuStats Cpu,
    IReadOnlyList<GpuAdapterStats> Gpus,
    MemoryStats Memory,
    DateTime Timestamp,
    IReadOnlyList<ProcessSample>? TopProcesses = null);