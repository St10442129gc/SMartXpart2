using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartX.DeviceSimulator;

public record DeviceCommandMessage(Guid CommandId, string MacAddress, bool TargetState, string Action, DateTime IssuedUtc);

public record SimulatorScenario(Guid Id, string Type, string? MacAddress, int? Count, DateTime RequestedUtc);

/// <summary>
/// Every call here is exactly what device firmware would send: plain JSON over HTTP to the gateway API.
/// Nothing is injected into the API's memory directly.
/// </summary>
public class GatewayClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public GatewayClient(string baseAddress)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(10) };
    }

    public string BaseAddress => _http.BaseAddress!.ToString();

    public async Task RegisterAsync(SimulatedDevice d)
    {
        var response = await _http.PostAsJsonAsync("/api/stream/devices", new
        {
            MacAddress = d.Mac,
            d.Name,
            d.Kind,
            d.Location,
            d.ExpectedMin,
            d.ExpectedMax
        }, Json);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> SendTelemetryAsync(SimulatedDevice d, double value, DateTime? timestampUtc = null)
    {
        var response = await _http.PostAsJsonAsync("/api/stream/telemetry", new
        {
            MacAddress = d.Mac,
            Value = Math.Round(value, 2),
            d.Unit,
            SwitchState = d.On,
            TimestampUtc = timestampUtc ?? DateTime.UtcNow
        }, Json);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ReportDisconnectAsync(SimulatedDevice d)
    {
        var response = await _http.PostAsync($"/api/stream/devices/{Uri.EscapeDataString(d.Mac)}/disconnect", null);
        if (!response.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("alertRaised").GetBoolean();
    }

    public async Task<List<DeviceCommandMessage>> PollCommandsAsync(SimulatedDevice d) =>
        await _http.GetFromJsonAsync<List<DeviceCommandMessage>>(
            $"/api/stream/devices/{Uri.EscapeDataString(d.Mac)}/commands", Json) ?? new();

    public async Task<List<SimulatorScenario>> PollScenariosAsync() =>
        await _http.GetFromJsonAsync<List<SimulatorScenario>>("/api/simulator/scenarios/next", Json) ?? new();

    public async Task<bool> RequestScenarioAsync(string type) =>
        (await _http.PostAsJsonAsync("/api/simulator/scenarios", new { Type = type }, Json)).IsSuccessStatusCode;
}
