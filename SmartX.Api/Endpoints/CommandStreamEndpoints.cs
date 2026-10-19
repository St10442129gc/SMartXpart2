using SmartX.Api.Models;
using SmartX.Api.Services;
using SmartX.Api.Services.CommandStream;
using SmartX.Api.Services.Insights;

namespace SmartX.Api.Endpoints;

/// <summary>Part 2: Real-Time Command Stream &amp; History API surface.</summary>
public static class CommandStreamEndpoints
{
    public static void MapCommandStreamEndpoints(this WebApplication app)
    {
        MapDeviceFacing(app);
        MapDashboardFacing(app);
        MapCommands(app);
        MapInsights(app);
        MapSimulatorControl(app);
    }

    // -----------------------------------------------------------------
    // Device-facing: what an ESP32 / smart plug (or the simulator) calls
    // -----------------------------------------------------------------
    private static void MapDeviceFacing(WebApplication app)
    {
        var devices = app.MapGroup("/api/stream").WithTags("Command Stream - devices");

        // A device announces itself on boot. Idempotent: re-registering the same MAC just refreshes its details.
        devices.MapPost("/devices", (RegisterDeviceRequest request, DeviceRegistry registry, TelemetryStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.MacAddress))
                return Results.BadRequest("MacAddress is required.");

            var mac = DeviceRegistry.NormaliseMac(request.MacAddress);

            // Mirror the device into the Part 1 sensor list so its health score and readings show there too.
            var profile = store.Sensors.Values.FirstOrDefault(s => DeviceRegistry.NormaliseMac(s.MacAddress) == mac);
            if (profile is null)
            {
                profile = new SensorProfile { MacAddress = mac, DeploymentLocation = request.Location, Category = request.Kind.ToCategory() };
                store.RegisterSensor(profile);
            }

            var view = registry.Register(request with { MacAddress = mac }, profile.Id);
            return Results.Ok(view);
        })
        .WithName("RegisterStreamDevice");

        // Every reporting tick. Returns 202 because the packet is only QUEUED here - the background processor handles it.
        devices.MapPost("/telemetry", (DeviceTelemetryRequest request, TelemetryIngestionQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(request.MacAddress))
                return Results.BadRequest("MacAddress is required.");

            var result = queue.Enqueue(request);
            return result is null
                ? Results.NotFound($"Unknown device '{request.MacAddress}'. Register it first via POST /api/stream/devices.")
                : Results.Accepted(value: result);
        })
        .WithName("SubmitStreamTelemetry");

        // A device (or its parent node) reporting a lost link. Repeats for an already-offline node are suppressed.
        devices.MapPost("/devices/{mac}/disconnect", (string mac, StreamProcessor processor, DeviceRegistry registry) =>
        {
            if (!registry.Contains(mac)) return Results.NotFound();
            var raised = processor.HandleDisconnect(mac, "Device reported link lost");
            return Results.Ok(new
            {
                MacAddress = DeviceRegistry.NormaliseMac(mac),
                AlertRaised = raised,
                Note = raised ? "New disconnect alert raised." : "Duplicate ignored - node is already in the disconnected set."
            });
        })
        .WithName("ReportDeviceDisconnect");

        // Devices poll this to receive manual override commands, oldest first.
        devices.MapGet("/devices/{mac}/commands", (string mac, CommandService commands) =>
            Results.Ok(commands.CollectPending(mac)))
        .WithName("PollDeviceCommands");
    }

    // -----------------------------------------------------------------
    // Dashboard-facing reads
    // -----------------------------------------------------------------
    private static void MapDashboardFacing(WebApplication app)
    {
        var stream = app.MapGroup("/api/stream").WithTags("Command Stream - dashboard");

        // Search / filter by free text, operational category and alert state.
        stream.MapGet("/devices", (string? search, string? category, string? alert, DeviceRegistry registry, FaultTracker faults) =>
        {
            IEnumerable<DeviceView> query = registry.All();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(d =>
                    d.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.MacAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.Location.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.Kind.ToString().Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<SensorCategory>(category, true, out var cat))
                    return Results.BadRequest($"Unknown category '{category}'.");
                query = query.Where(d => d.Category == cat);
            }

            if (!string.IsNullOrWhiteSpace(alert) && !alert.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var faulty = faults.MacsWithFaults();
                query = alert.ToLowerInvariant() switch
                {
                    "healthy" => query.Where(d => d.IsOnline && d.AlertLevel == AlertLevel.None && !faulty.Contains(d.MacAddress)),
                    "alerting" => query.Where(d => d.AlertLevel != AlertLevel.None || faulty.Contains(d.MacAddress)),
                    "warning" => query.Where(d => d.AlertLevel == AlertLevel.Warning),
                    "critical" => query.Where(d => d.AlertLevel == AlertLevel.Critical && d.IsOnline),
                    "disconnected" => query.Where(d => faults.IsDisconnected(d.MacAddress)),
                    _ => query
                };
            }

            return Results.Ok(query
                .OrderByDescending(d => d.AlertLevel)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
        })
        .WithName("SearchStreamDevices");

        // Last few readings for every device in one call, for the sparklines on the dashboard cards.
        stream.MapGet("/sparklines", (int? take, DeviceRegistry registry, TelemetryHistory history) =>
        {
            var n = Math.Clamp(take ?? 24, 2, 120);
            return Results.Ok(registry.All().ToDictionary(
                d => d.MacAddress,
                d => history.Range(d.MacAddress, takeLast: n).Select(p => p.Value).ToArray()));
        })
        .WithName("GetStreamSparklines");

        stream.MapGet("/devices/{mac}", (string mac, DeviceRegistry registry) =>
            registry.Find(mac) is { } d ? Results.Ok(d) : Results.NotFound())
        .WithName("GetStreamDevice");

        // Timeline for one device, oldest first, straight off the SortedDictionary keys.
        stream.MapGet("/devices/{mac}/history", (string mac, int? minutes, int? take, DeviceRegistry registry, TelemetryHistory history) =>
        {
            var device = registry.Find(mac);
            if (device is null) return Results.NotFound();
            var from = DateTime.UtcNow.AddMinutes(-(minutes is > 0 ? minutes.Value : 10));
            return Results.Ok(new
            {
                device.MacAddress,
                device.Name,
                device.Unit,
                device.ExpectedMin,
                device.ExpectedMax,
                Points = history.Range(device.MacAddress, fromUtc: from, takeLast: take is > 0 ? take : 300)
            });
        })
        .WithName("GetStreamDeviceHistory");

        // Everything the dashboard's status strip needs, in one round trip.
        stream.MapGet("/overview", (TelemetryIngestionQueue queue, StreamProcessor processor, FaultTracker faults,
            DeviceRegistry registry, CommandService commands, SimulatorControl simulator) =>
        {
            var all = registry.All();
            return Results.Ok(new
            {
                Queue = queue.Stats(),
                Processor = processor.Stats(),
                Faults = faults.Stats(),
                ActiveFaults = faults.ActiveFaults(),
                DisconnectedNodes = faults.DisconnectedNodes(),
                DevicesTotal = all.Count,
                DevicesOnline = all.Count(d => d.IsOnline),
                UndoDepth = commands.UndoDepth,
                NextUndo = commands.PeekUndo()?.Label,
                RecentlyProcessed = processor.RecentlyProcessed(25),
                Simulator = simulator.Status()
            });
        })
        .WithName("GetStreamOverview");

        stream.MapPost("/processing/pause", (StreamProcessor p) => { p.SetPaused(true); return Results.Ok(p.Stats()); })
            .WithName("PauseProcessing");
        stream.MapPost("/processing/resume", (StreamProcessor p) => { p.SetPaused(false); return Results.Ok(p.Stats()); })
            .WithName("ResumeProcessing");
        stream.MapPost("/processing/settings", (ProcessingSettingsRequest request, StreamProcessor p) =>
        {
            if (request.StandardLaneDelayMs is int ms) p.SetStandardLaneDelay(ms);
            return Results.Ok(p.Stats());
        })
        .WithName("UpdateProcessingSettings");
    }

    // -----------------------------------------------------------------
    // Manual override commands + undo
    // -----------------------------------------------------------------
    private static void MapCommands(WebApplication app)
    {
        var commands = app.MapGroup("/api/commands").WithTags("Command Stream - commands");

        commands.MapPost("/", (IssueCommandRequest request, CommandService service, ActionInsightsEngine insights) =>
        {
            var result = service.Issue(request);
            if (!result.Ok) return Results.BadRequest(result);
            insights.RecordCommand(result.Command!); // feeds the pattern analyser
            return Results.Ok(result);
        })
        .WithName("IssueCommand");

        commands.MapPost("/undo", (CommandService service) =>
        {
            var result = service.Undo();
            return result.Ok ? Results.Ok(result) : Results.Conflict(result);
        })
        .WithName("UndoLastCommand");

        commands.MapGet("/history", (CommandService service) => Results.Ok(new
        {
            NextUndo = service.PeekUndo(),
            UndoStack = service.UndoStack().Take(15),
            Audit = service.AuditLog(30),
            SupportedActions = CommandService.SupportedActions
        }))
        .WithName("GetCommandHistory");
    }

    // -----------------------------------------------------------------
    // Suggested actions / automated insights
    // -----------------------------------------------------------------
    private static void MapInsights(WebApplication app)
    {
        var insights = app.MapGroup("/api/insights").WithTags("Insights");

        insights.MapGet("/", (ActionInsightsEngine engine) => Results.Ok(engine.GetInsights()))
            .WithName("GetInsights");

        // The dashboard reports what the operator searched for so the engine can learn from it.
        insights.MapPost("/search", (SearchEventRequest request, ActionInsightsEngine engine) =>
        {
            if (string.IsNullOrWhiteSpace(request.Term)) return Results.BadRequest("Term is required.");
            return Results.Ok(engine.RecordSearch(request.Term));
        })
        .WithName("RecordSearch");

        insights.MapPost("/dismiss", (DismissRequest request, ActionInsightsEngine engine) =>
        {
            engine.Dismiss(request.Id);
            return Results.NoContent();
        })
        .WithName("DismissSuggestion");

        insights.MapPost("/train-sample", (ActionInsightsEngine engine) => Results.Ok(new { Message = engine.TrainSampleHistory() }))
            .WithName("LoadSampleOperatorHistory");
    }

    // -----------------------------------------------------------------
    // Test-bench mailbox for the device simulator
    // -----------------------------------------------------------------
    private static void MapSimulatorControl(WebApplication app)
    {
        var sim = app.MapGroup("/api/simulator").WithTags("Simulator control");

        sim.MapPost("/scenarios", (ScenarioRequest request, SimulatorControl control) =>
        {
            var scenario = control.Request(request);
            return scenario is null
                ? Results.BadRequest($"Unknown scenario '{request.Type}'. Use one of: {string.Join(", ", SimulatorControl.ScenarioTypes)}.")
                : Results.Accepted(value: scenario);
        })
        .WithName("RequestSimulatorScenario");

        sim.MapGet("/scenarios/next", (SimulatorControl control) => Results.Ok(control.TakePending()))
            .WithName("TakeSimulatorScenarios");

        sim.MapGet("/status", (SimulatorControl control) => Results.Ok(control.Status()))
            .WithName("GetSimulatorStatus");
    }
}
