namespace SmartX.DeviceSimulator;

/// <summary>
/// A small hydroponics / smart-building site. The devices are physically linked so operator commands have
/// visible consequences on the dashboard:
///   * Pump A waters Hydroponics Bay B - Moisture Sensor B dries out unless Pump A is running.
///   * Valve 1 feeds Bay A - closing it ("shut down all valves") makes Moisture Sensor A fall.
///   * The heater smart plug draws ~1.2 kW and is the default target for an injected power spike.
/// </summary>
public class Fleet
{
    private readonly object _lock = new();
    private readonly Random _rng = new();

    public List<SimulatedDevice> Devices { get; } = new()
    {
        new() { Mac = "24:6F:28:A1:00:01", Name = "Moisture Sensor A", Kind = DeviceKind.MoistureSensor, Location = "Hydroponics Bay A", ExpectedMin = 30, ExpectedMax = 85, Value = 62, Baseline = 62 },
        new() { Mac = "24:6F:28:A1:00:02", Name = "Moisture Sensor B", Kind = DeviceKind.MoistureSensor, Location = "Hydroponics Bay B", ExpectedMin = 30, ExpectedMax = 85, Value = 48, Baseline = 48 },
        new() { Mac = "24:6F:28:A1:00:03", Name = "Greenhouse Temp 1", Kind = DeviceKind.TemperatureSensor, Location = "Greenhouse North", ExpectedMin = 15, ExpectedMax = 30, Value = 23, Baseline = 23 },
        new() { Mac = "24:6F:28:A1:00:04", Name = "Greenhouse Temp 2", Kind = DeviceKind.TemperatureSensor, Location = "Greenhouse South", ExpectedMin = 15, ExpectedMax = 30, Value = 25, Baseline = 25 },
        new() { Mac = "50:02:91:B2:00:01", Name = "Grow Lights Plug", Kind = DeviceKind.SmartPlug, Location = "Greenhouse North", ExpectedMax = 2000, Value = 420, Baseline = 420, On = true },
        new() { Mac = "50:02:91:B2:00:02", Name = "Heater Plug", Kind = DeviceKind.SmartPlug, Location = "Greenhouse South", ExpectedMax = 2000, Value = 1200, Baseline = 1200, On = true },
        new() { Mac = "24:6F:28:C3:00:01", Name = "Pump A", Kind = DeviceKind.WaterPump, Location = "Hydroponics Bay B", On = false },
        new() { Mac = "24:6F:28:C3:00:02", Name = "Pump B", Kind = DeviceKind.WaterPump, Location = "Reservoir", On = true },
        new() { Mac = "24:6F:28:C3:00:03", Name = "Ventilation Relay", Kind = DeviceKind.SmartRelay, Location = "Greenhouse North", On = false },
        new() { Mac = "24:6F:28:D4:00:01", Name = "Valve 1", Kind = DeviceKind.Valve, Location = "Hydroponics Bay A", On = true },
        new() { Mac = "24:6F:28:D4:00:02", Name = "Valve 2", Kind = DeviceKind.Valve, Location = "Hydroponics Bay B", On = true },
        new() { Mac = "24:6F:28:D4:00:03", Name = "Valve 3", Kind = DeviceKind.Valve, Location = "Reservoir", On = false },
    };

    public SimulatedDevice? Find(string? macOrName) =>
        string.IsNullOrWhiteSpace(macOrName)
            ? null
            : Devices.FirstOrDefault(d => d.Mac.Equals(macOrName, StringComparison.OrdinalIgnoreCase)
                                          || d.Name.Equals(macOrName, StringComparison.OrdinalIgnoreCase));

    private SimulatedDevice Named(string name) => Devices.First(d => d.Name == name);

    /// <summary>Advances the physics by one reporting tick and returns what each online device will report.</summary>
    public List<(SimulatedDevice device, double value)> Step()
    {
        lock (_lock)
        {
            var pumpA = Named("Pump A");
            var valve1 = Named("Valve 1");

            foreach (var d in Devices)
            {
                switch (d.Kind)
                {
                    case DeviceKind.MoistureSensor:
                        var watered = d.Name == "Moisture Sensor B" ? pumpA.On == true : valve1.On == true;
                        // Watering pushes moisture up quickly; otherwise the substrate slowly dries out.
                        d.Value += watered ? 2.5 : (d.Name == "Moisture Sensor B" ? -0.7 : -0.35);
                        d.Value = Math.Clamp(d.Value + Noise(0.3), 5, watered ? 80 : 100);
                        break;

                    case DeviceKind.TemperatureSensor:
                        d.Value += (d.Baseline - d.Value) * 0.2 + Noise(0.4);
                        break;

                    case DeviceKind.SmartPlug:
                        if (d.On != true) d.Value = 0;
                        else if (d.SpikeTicks > 0) { d.Value = 3200 + _rng.Next(0, 600); d.SpikeTicks--; }
                        else d.Value = d.Baseline + Noise(d.Baseline * 0.03);
                        break;

                    case DeviceKind.WaterPump:
                        d.Value = d.On == true ? 12 + Noise(0.5) : 0;
                        break;

                    default: // relays and valves just report their state
                        d.Value = d.On == true ? 1 : 0;
                        break;
                }
            }

            return Devices.Where(d => !d.Silent).Select(d => (d, d.Value)).ToList();
        }
    }

    public void ApplyCommand(SimulatedDevice d, bool targetState)
    {
        lock (_lock)
        {
            d.On = targetState;
            if (d.Kind == DeviceKind.SmartPlug && !targetState) d.SpikeTicks = 0;
        }
    }

    // ---------------------------------------------------------------------
    // Test-bench scenarios
    // ---------------------------------------------------------------------

    public SimulatedDevice InjectPowerSpike(string? target)
    {
        lock (_lock)
        {
            var plug = Find(target) is { Kind: DeviceKind.SmartPlug } p ? p : Named("Heater Plug");
            plug.On = true;
            plug.SpikeTicks = 2;
            return plug;
        }
    }

    public SimulatedDevice InjectMoistureDrop(string? target)
    {
        lock (_lock)
        {
            var sensor = Find(target) is { Kind: DeviceKind.MoistureSensor } s ? s : Named("Moisture Sensor B");
            sensor.Value = 18;
            // Stop whatever waters it, so it stays dry until the operator reacts.
            if (sensor.Name == "Moisture Sensor B") Named("Pump A").On = false;
            else Named("Valve 1").On = false;
            return sensor;
        }
    }

    public SimulatedDevice GoSilent(string? target)
    {
        lock (_lock)
        {
            var d = Find(target) ?? Named("Valve 3");
            d.Silent = true;
            return d;
        }
    }

    public List<SimulatedDevice> ReconnectAll()
    {
        lock (_lock)
        {
            var back = Devices.Where(d => d.Silent).ToList();
            back.ForEach(d => d.Silent = false);
            return back;
        }
    }

    public void ResetBaselines()
    {
        lock (_lock)
        {
            foreach (var d in Devices)
            {
                d.Silent = false;
                d.SpikeTicks = 0;
                if (d.Kind is DeviceKind.MoistureSensor or DeviceKind.TemperatureSensor) d.Value = d.Baseline;
            }
        }
    }

    /// <summary>A flood of ordinary (in-range) readings from every sensor at once.</summary>
    public List<(SimulatedDevice device, double value, DateTime ts)> BuildBurst(int perDevice)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var packets = new List<(SimulatedDevice, double, DateTime)>();
            // Only devices whose current reading is comfortably ordinary, so the whole burst stays in the FIFO lane
            // and never changes what the dashboard thinks is going on.
            var sensors = Devices.Where(d => !d.Silent && (
                (d.Kind == DeviceKind.MoistureSensor && d.Value > 24) ||
                d.Kind == DeviceKind.TemperatureSensor ||
                (d.Kind == DeviceKind.SmartPlug && d.On == true && d.SpikeTicks == 0))).ToList();
            for (var i = 0; i < perDevice; i++)
                foreach (var d in sensors)
                {
                    var v = d.Value + Noise(d.Kind == DeviceKind.SmartPlug ? 15 : 0.3);
                    packets.Add((d, v, now.AddMilliseconds(i)));
                }
            return packets;
        }
    }

    private double Noise(double amplitude) => (_rng.NextDouble() * 2 - 1) * amplitude;
}
