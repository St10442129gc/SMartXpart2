namespace SmartX.DeviceSimulator;

/// <summary>Mirrors SmartX.Api's DeviceKind enum (sent over JSON as a string).</summary>
public enum DeviceKind
{
    MoistureSensor,
    TemperatureSensor,
    SmartPlug,
    WaterPump,
    SmartRelay,
    Valve
}

/// <summary>One pretend piece of hardware: an ESP32 node, smart plug, pump, relay or valve.</summary>
public class SimulatedDevice
{
    public required string Mac { get; init; }
    public required string Name { get; init; }
    public required DeviceKind Kind { get; init; }
    public required string Location { get; init; }
    public double? ExpectedMin { get; init; }
    public double? ExpectedMax { get; init; }

    /// <summary>Current physical reading (moisture %, temperature, watts, flow).</summary>
    public double Value { get; set; }

    /// <summary>Baseline the reading wanders around when nothing is wrong.</summary>
    public double Baseline { get; set; }

    /// <summary>On / Open for actuators. Null for pure sensors.</summary>
    public bool? On { get; set; }

    /// <summary>When true the device has "lost its link" and stops reporting.</summary>
    public bool Silent { get; set; }

    /// <summary>Remaining ticks of an injected power spike.</summary>
    public int SpikeTicks { get; set; }

    public bool IsActuator => Kind is DeviceKind.WaterPump or DeviceKind.SmartRelay or DeviceKind.Valve or DeviceKind.SmartPlug;

    public string Unit => Kind switch
    {
        DeviceKind.MoistureSensor => "%",
        DeviceKind.TemperatureSensor => "°C",
        DeviceKind.SmartPlug => "W",
        DeviceKind.WaterPump => "L/min",
        _ => "state"
    };

    public string Describe() =>
        On is null ? $"{Name,-20} {Value,8:0.0}{Unit}" : $"{Name,-20} {(On.Value ? "ON " : "OFF"),8} {Value,6:0.0}{Unit}";
}
