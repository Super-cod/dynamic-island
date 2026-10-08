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

public sealed record TelemetrySnapshot(
    CpuStats Cpu,
    IReadOnlyList<GpuAdapterStats> Gpus,
    MemoryStats Memory,
    DateTime Timestamp);