using SmartX.DeviceSimulator;

// =====================================================================
// Smart-X device simulator
//
// Pretends to be a fleet of ESP32 sensors, smart plugs, pumps, relays and
// valves. Everything it does goes over HTTP to SmartX.Api exactly like
// real firmware would: it registers on boot, reports telemetry every tick,
// polls for manual override commands and acknowledges them by reporting
// its new state, and reports a lost link when it "disconnects".
//
// Usage:  dotnet run --project SmartX.DeviceSimulator [apiBaseUrl] [tickMs]
//   env:  SMARTX_API (default http://localhost:5080), SMARTX_TICK_MS (default 2000)
// =====================================================================

var apiBase = args.ElementAtOrDefault(0) ?? Environment.GetEnvironmentVariable("SMARTX_API") ?? "http://localhost:5080";
var tickMs = int.TryParse(args.ElementAtOrDefault(1) ?? Environment.GetEnvironmentVariable("SMARTX_TICK_MS"), out var t) ? t : 2000;

var gateway = new GatewayClient(apiBase);
var fleet = new Fleet();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Log($"Smart-X device simulator -> {gateway.BaseAddress} (tick {tickMs} ms, {fleet.Devices.Count} devices)");

// ---- Boot: register every device, retrying until the API is reachable ----
while (!cts.IsCancellationRequested)
{
    try
    {
        foreach (var d in fleet.Devices) await gateway.RegisterAsync(d);
        Log("All devices registered with the gateway.");
        break;
    }
    catch (Exception ex)
    {
        Log($"API not reachable yet ({ex.GetBaseException().Message}) - retrying in 3s...");
        try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { }
    }
}

if (!Console.IsInputRedirected)
{
    Log("Keys: [s] power spike  [m] moisture drop  [d] disconnect  [u] duplicate disconnects  [b] burst  [p] burst + spike  [r] reconnect  [q] quit");
    _ = Task.Run(KeyboardLoop);
}

var scenarioLoop = Task.Run(ScenarioLoop);
var tick = 0;

while (!cts.IsCancellationRequested)
{
    try
    {
        await PollAndApplyCommandsAsync();

        var readings = fleet.Step();
        await Parallel.ForEachAsync(readings, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token },
            async (r, _) => await gateway.SendTelemetryAsync(r.device, r.value));

        if (++tick % 10 == 0)
            Log($"tick {tick}: {readings.Count} devices reporting - " +
                string.Join(" | ", fleet.Devices.Where(d => d.Kind == DeviceKind.MoistureSensor).Select(d => $"{d.Name} {d.Value:0}%")));
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Log($"Lost contact with the gateway: {ex.GetBaseException().Message}. Re-registering...");
        try
        {
            foreach (var d in fleet.Devices) await gateway.RegisterAsync(d);
        }
        catch { /* still down - try again next tick */ }
    }

    try { await Task.Delay(tickMs, cts.Token); } catch (OperationCanceledException) { break; }
}

await scenarioLoop.ContinueWith(_ => { });
Log("Simulator stopped.");
return;

// ---------------------------------------------------------------------

async Task PollAndApplyCommandsAsync()
{
    foreach (var d in fleet.Devices.Where(d => d.IsActuator && !d.Silent))
    {
        foreach (var command in await gateway.PollCommandsAsync(d))
        {
            fleet.ApplyCommand(d, command.TargetState);
            Log($"<- command {command.Action} for {d.Name}: now {(command.TargetState ? "ON/OPEN" : "OFF/CLOSED")}");
        }
    }
}

async Task ScenarioLoop()
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            foreach (var scenario in await gateway.PollScenariosAsync())
                await RunScenarioAsync(scenario.Type, scenario.MacAddress, scenario.Count);
        }
        catch (Exception) { /* API may be restarting */ }

        try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
    }
}

async Task RunScenarioAsync(string type, string? target, int? count)
{
    switch (type)
    {
        case "PowerSpike":
        {
            var plug = fleet.InjectPowerSpike(target);
            var value = 3200 + Random.Shared.Next(0, 600);
            await gateway.SendTelemetryAsync(plug, value);
            Log($"!! {plug.Name} reported a {value} W power spike");
            break;
        }
        case "MoistureDrop":
        {
            var sensor = fleet.InjectMoistureDrop(target);
            await gateway.SendTelemetryAsync(sensor, sensor.Value);
            Log($"!! {sensor.Name} dropped to {sensor.Value:0}% moisture");
            break;
        }
        case "Disconnect":
        {
            var d = fleet.GoSilent(target);
            var raised = await gateway.ReportDisconnectAsync(d);
            Log($"xx {d.Name} lost its link (alert raised: {raised})");
            break;
        }
        case "DuplicateDisconnects":
        {
            var d = fleet.GoSilent(target);
            var raisedCount = 0;
            for (var i = 0; i < 5; i++)
                if (await gateway.ReportDisconnectAsync(d)) raisedCount++;
            Log($"xx {d.Name} sent 5 link-lost alerts - gateway raised {raisedCount}, suppressed {5 - raisedCount} duplicate(s)");
            break;
        }
        case "Burst":
        case "BurstWithSpike":
        {
            var burst = fleet.BuildBurst(count is > 0 ? count.Value : 12);
            await Parallel.ForEachAsync(burst, new ParallelOptions { MaxDegreeOfParallelism = 16 },
                async (p, _) => await gateway.SendTelemetryAsync(p.device, p.value, p.ts));
            Log($">> burst of {burst.Count} ordinary readings sent");

            if (type == "BurstWithSpike")
            {
                var plug = fleet.InjectPowerSpike(target);
                var value = 3500 + Random.Shared.Next(0, 400);
                await gateway.SendTelemetryAsync(plug, value);
                Log($"!! ...followed by a {value} W spike on {plug.Name} - watch it overtake the backlog");
            }
            break;
        }
        case "Reconnect":
        {
            var back = fleet.ReconnectAll();
            foreach (var d in back) await gateway.SendTelemetryAsync(d, d.Value);
            Log($"<> reconnected {back.Count} device(s)");
            break;
        }
        case "Reset":
            fleet.ResetBaselines();
            Log("<> all sensors reset to healthy baselines");
            break;
    }
}

async Task KeyboardLoop()
{
    while (!cts.IsCancellationRequested)
    {
        var key = Console.ReadKey(intercept: true).KeyChar;
        var scenario = char.ToLowerInvariant(key) switch
        {
            's' => "PowerSpike",
            'm' => "MoistureDrop",
            'd' => "Disconnect",
            'u' => "DuplicateDisconnects",
            'b' => "Burst",
            'p' => "BurstWithSpike",
            'r' => "Reconnect",
            'q' => null,
            _ => ""
        };
        if (scenario is null) { cts.Cancel(); break; }
        if (scenario.Length > 0) await RunScenarioAsync(scenario, null, null);
    }
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
