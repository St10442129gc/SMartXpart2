namespace SmartX.Api.Models;

// =====================================================================
// PART 2: Real-Time Command Stream domain model
// =====================================================================

/// <summary>What a physical device on the mesh actually is. Drives how it is triaged and controlled.</summary>
public enum DeviceKind
{
    MoistureSensor,
    TemperatureSensor,
    SmartPlug,
    WaterPump,
    SmartRelay,
    Valve
}

/// <summary>Ordered so a higher value is more serious - used directly as a sort key.</summary>
public enum AlertLevel
{
    None = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Which ingestion lane a packet travelled through.</summary>
public enum ProcessingLane
{
    Standard,
    Priority
}

/// <summary>Distinct fault states a device can be in. Combined with the MAC address to form a unique HashSet key.</summary>
public enum FaultCode
{
    Disconnected,
    OutOfRange,
    PowerSpike,
    CriticalMoisture
}

public static class DeviceKindExtensions
{
    public static SensorCategory ToCategory(this DeviceKind kind) => kind switch
    {
        DeviceKind.MoistureSensor or DeviceKind.TemperatureSensor => SensorCategory.Environmental,
        DeviceKind.SmartPlug => SensorCategory.PowerConsumption,
        _ => SensorCategory.Actuator
    };

    /// <summary>Actuators accept manual override commands (on/off, open/close).</summary>
    public static bool IsActuator(this DeviceKind kind) =>
        kind is DeviceKind.WaterPump or DeviceKind.SmartRelay or DeviceKind.Valve or DeviceKind.SmartPlug;

    public static string DefaultUnit(this DeviceKind kind) => kind switch
    {
        DeviceKind.MoistureSensor => "%",
        DeviceKind.TemperatureSensor => "°C",
        DeviceKind.SmartPlug => "W",
        DeviceKind.WaterPump => "L/min",
        _ => "state"
    };
}

/// <summary>
/// The live, mutable state of one device on the mesh. Owned by <see cref="Services.CommandStream.DeviceRegistry"/>
/// and only ever mutated under the registry's lock; the outside world gets immutable <see cref="DeviceView"/> snapshots.
/// </summary>
public class LiveDevice
{
    public string MacAddress { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DeviceKind Kind { get; init; }
    public SensorCategory Category => Kind.ToCategory();
    public string Location { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double? ExpectedMin { get; set; }
    public double? ExpectedMax { get; set; }

    /// <summary>Links the device to its Part 1 SensorProfile so health scores and the Part 1 feed stay in sync.</summary>
    public string? SensorProfileId { get; set; }

    public double? LastValue { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Device timestamp of the newest reading applied to live state. Older (late) packets only go to history.</summary>
    public DateTime? LatestReadingUtc { get; set; }

    public bool IsOnline { get; set; }

    /// <summary>On/Off (or Open/Closed) state the device itself last reported.</summary>
    public bool? ReportedState { get; set; }

    /// <summary>State the operator last commanded. When it differs from ReportedState the device has not caught up yet.</summary>
    public bool? DesiredState { get; set; }

    public AlertLevel AlertLevel { get; set; }
    public string? AlertMessage { get; set; }
    public long PacketsProcessed { get; set; }

    public DeviceView ToView(int? healthScore) => new(
        MacAddress, Name, Kind, Category, Location, LastValue, Unit, LastSeenUtc, IsOnline,
        Kind.IsActuator(), ReportedState, DesiredState,
        DesiredState is null || DesiredState == ReportedState,
        ExpectedMin, ExpectedMax, AlertLevel, AlertMessage, PacketsProcessed, healthScore);
}

public record DeviceView(
    string MacAddress, string Name, DeviceKind Kind, SensorCategory Category, string Location,
    double? LastValue, string Unit, DateTime? LastSeenUtc, bool IsOnline,
    bool IsActuator, bool? ReportedState, bool? DesiredState, bool InSync,
    double? ExpectedMin, double? ExpectedMax, AlertLevel AlertLevel, string? AlertMessage,
    long PacketsProcessed, int? HealthScore);

/// <summary>A telemetry packet waiting in one of the two ingestion lanes.</summary>
public sealed class QueuedPacket
{
    public long Sequence { get; init; }
    public string MacAddress { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public bool? SwitchState { get; init; }
    public DateTime DeviceTimestampUtc { get; init; }
    public DateTime EnqueuedUtc { get; init; } = DateTime.UtcNow;
    public ProcessingLane Lane { get; set; }
    public AlertLevel Severity { get; set; }
    public string? TriageReason { get; set; }

    /// <summary>True when the packet came in through the Part 1 /api/telemetry endpoint, which already applied its own health score.</summary>
    public bool FromLegacyEndpoint { get; init; }
}

/// <summary>One line of the processing log - proves which lane a packet went through and how long it waited.</summary>
public record ProcessedRecord(
    long Sequence, string MacAddress, string DeviceName, double Value, string Unit,
    ProcessingLane Lane, AlertLevel Severity, string? Reason, double WaitedMs, DateTime ProcessedUtc,
    bool ArchivedOnly = false);

// ---------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------

public record RegisterDeviceRequest(string MacAddress, string Name, DeviceKind Kind, string Location,
    double? ExpectedMin = null, double? ExpectedMax = null);

/// <summary>What a device (or the simulator pretending to be one) sends on every reporting tick.</summary>
public record DeviceTelemetryRequest(string MacAddress, double Value, string? Unit = null,
    bool? SwitchState = null, DateTime? TimestampUtc = null);

public record IssueCommandRequest(string Action, string? MacAddress = null, string? Source = null);

public record SearchEventRequest(string Term);

public record DismissRequest(string Id);

public record ProcessingSettingsRequest(int? StandardLaneDelayMs);
