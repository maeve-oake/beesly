using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using System.Diagnostics;
using System.Text.Json;

namespace Beesly;

public sealed class HomeAssistantService(AppSettings settings, IHomeAssistantApiManager api)
{
    public IReadOnlyList<HomeAssistantEntity> Entities => settings.Entities;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.HaToken);

    public async Task<List<HassState>> GetStatesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await api.GetApiCallAsync<List<HassState>>("states", timeout.Token)
            ?? throw new JsonException("Missing states response.");
    }

    public async Task ToggleAsync(HomeAssistantEntity entity)
    {
        if (!entity.CanToggle)
            throw new InvalidOperationException("This entity cannot be controlled.");
        if (entity.Domain == "climate")
        {
            var state = (await GetStatesAsync()).FirstOrDefault(s => s.EntityId == entity.EntityId)?.State;
            if (!entity.CanControlState(state))
                throw new HttpRequestException("This climate entity is unavailable.");
            await CallServiceAsync("climate/set_hvac_mode",
                new { entity_id = entity.EntityId, hvac_mode = state == "off" ? "cool" : "off" });
            return;
        }
        await CallServiceAsync($"{entity.Domain}/toggle", new { entity_id = entity.EntityId });
    }

    public async Task SetTemperatureAsync(HomeAssistantEntity entity, double temperature)
    {
        if (!entity.CanToggle || entity.Domain != "climate" || !double.IsFinite(temperature))
            throw new InvalidOperationException("Invalid temperature control.");
        await CallServiceAsync("climate/set_temperature", new { entity_id = entity.EntityId, temperature });
    }

    public async Task SetColorAsync(IReadOnlyList<HomeAssistantEntity> entities, string color)
    {
        if (entities.Count == 0 || entities.Any(entity => !entity.CanToggle || entity.Domain != "light"))
            throw new InvalidOperationException("Invalid colour control.");
        var ids = entities.Select(entity => entity.EntityId).ToArray();
        object data = color switch
        {
            "warm-white" => new { entity_id = ids, color_temp_kelvin = 2700, transition = 0 },
            "cold-white" => new { entity_id = ids, color_temp_kelvin = 6500, transition = 0 },
            "purple" => new { entity_id = ids, rgb_color = new[] { 160, 32, 240 }, transition = 0 },
            "red" => new { entity_id = ids, rgb_color = new[] { 255, 0, 0 }, transition = 0 },
            "light-blue" => new { entity_id = ids, rgb_color = new[] { 100, 190, 255 }, transition = 0 },
            _ => throw new InvalidOperationException("Unknown colour preset.")
        };
        await CallServiceAsync("light/turn_on", data);
    }

    public async Task AdjustBrightnessAsync(IReadOnlyList<HomeAssistantEntity> entities, int step, CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0 || entities.Any(entity => !entity.CanToggle || entity.Domain != "light") || step is 0 or < -100 or > 100)
            throw new InvalidOperationException("Invalid brightness adjustment.");
        await CallServiceAsync("light/turn_on",
            new { entity_id = entities.Select(entity => entity.EntityId).ToArray(), brightness_step_pct = step, transition = 0.2 }, cancellationToken);
    }

    public async Task<List<HassState>> ToggleAndWaitAsync(HomeAssistantEntity entity, string previousState)
    {
        await ToggleAsync(entity);
        // Allow a short window for devices whose state update follows the service response.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var states = await GetStatesAsync();
            if (states.FirstOrDefault(s => s.EntityId == entity.EntityId)?.State != previousState || elapsed.Elapsed >= TimeSpan.FromSeconds(2))
                return states;
            await Task.Delay(150);
        }
    }

    private async Task CallServiceAsync(string service, object data, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var result = await api.PostApiCallAsync<List<HassState>>($"services/{service}", linked.Token, data);
        cancellationToken.ThrowIfCancellationRequested();
        // NetDaemon returns null for failed POSTs; an empty list is a successful response.
        if (result is null)
            throw new HttpRequestException($"Home Assistant rejected {service}.");
    }
}
