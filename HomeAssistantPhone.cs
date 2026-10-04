using CiscoIPPhone;
using CiscoIPPhoneApi;
using NetDaemon.Client.HomeAssistant.Model;
using NetDaemon.Client.Exceptions;

namespace Beesly;

public static class HomeAssistantPhone
{
    private const int PageSize = 8;
    public static string Url(HttpRequest request, string path) => $"{request.Scheme}://{request.Host}{request.PathBase}{path}";
    private static string Truncate(string value) => value.Length <= 32 ? value : value[..31] + "…";
    private static string EntityName(HomeAssistantEntity entity, HassState? state)
    {
        if (entity.Name is not null) return entity.Name;
        if (state?.AttributesJson is { } attributes && attributes.TryGetProperty("friendly_name", out var name)
            && name.ValueKind == System.Text.Json.JsonValueKind.String)
            return name.GetString() ?? entity.EntityId;
        return entity.EntityId;
    }

    private static void AddSoftKey(CiscoIpPhoneDisplayableType screen, string name, string url, ushort position) =>
        screen.SoftKeyItem.Add(new CiscoIpPhoneSoftKeyType { Name = name, Url = url, Position = position });
    private static string BrightnessNotify(HttpRequest request, string level)
    {
        var path = $"{request.PathBase}/ha/brightness/{level}".TrimStart('/');
        return $"Notify:http:{request.Host.Host}:{request.Host.Port ?? 80}:{path}::";
    }

    public static void MapHomeAssistant(this WebApplication app)
    {
        app.MapGet("/ha.xml", ShowEntitiesAsync);
        app.MapGet("/ha/touch.xml", ShowLampAsync);
        app.MapGet("/ha/aircon.xml", ShowAirconAsync);
        app.MapGet("/ha/{appliance}/{state}.png", GetGraphic);
        app.MapPost("/ha/brightness/{level}", AdjustBrightnessAsync);
    }

    private static IResult GetGraphic(HttpRequest request, IWebHostEnvironment environment, string appliance, string state)
    {
        if (appliance is not ("lamp" or "aircon") || state is not ("on" or "off" or "unavailable"))
            return Results.NotFound();

        request.HttpContext.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(Path.Combine(environment.ContentRootPath, "Assets", appliance, state + ".png"), "image/png");
    }

    private static string GraphicUrl(HttpRequest request, IWebHostEnvironment environment, string appliance, string state)
    {
        var path = Path.Combine(environment.ContentRootPath, "Assets", appliance, state + ".png");
        return Url(request, $"/ha/{appliance}/{state}.png?v={File.GetLastWriteTimeUtc(path).Ticks}");
    }

    private static async Task<IResult> AdjustBrightnessAsync(
        HttpRequest request, HomeAssistantService ha, ILogger<HomeAssistantService> logger, string level)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (level is not ("dim" or "brighter")) return Results.BadRequest();
        var entity = ha.Slots.GetValueOrDefault("01");
        if (entity is null || entity.Domain != "light") return Results.NotFound();
        if (!ha.IsConfigured) return Results.StatusCode(503);
        try
        {
            await ha.AdjustBrightnessAsync(entity, level == "dim" ? -20 : 20);
            // Notify ignores this acknowledgement; no displayable XML or navigation.
            return Results.Text("OK", "text/plain");
        }
        catch (Exception ex) when (ex is HttpRequestException or HomeAssistantApiCallException or OperationCanceledException)
        {
            logger.LogWarning("Brightness change failed for {Entity}: {Reason}", entity.EntityId, ex.Message);
            return Results.StatusCode(502);
        }
    }

    private static Task<IResult> ShowLampAsync(
        HttpRequest request, HomeAssistantService ha, IWebHostEnvironment environment,
        bool toggle = false, string? color = null) =>
        HandleRequestAsync(request, ha, async () =>
        {
            var entity = ha.Slots.GetValueOrDefault("01");
            if (entity is null || entity.Domain != "light")
                return Message(request, "Control unavailable", "The lamp is not enabled for toggling.");
            if (color is not null)
            {
                if (color is not ("warm-white" or "cold-white" or "purple" or "red" or "light-blue"))
                    return Results.BadRequest();
                await ha.SetColorAsync(entity, color);
            }
            var states = await ha.GetStatesAsync();
            var state = states.FirstOrDefault(s => s.EntityId == entity.EntityId)?.State;
            if (toggle && state is "on" or "off")
            {
                states = await ha.ToggleAndWaitAsync(entity, state);
                state = states.FirstOrDefault(s => s.EntityId == entity.EntityId)?.State;
            }
            var graphic = state is "on" or "off" ? state : "unavailable";
            var page = new CiscoIpPhoneGraphicFileMenu
            {
                Title = "Floor lamp menu",
                Prompt = "Tap the lamp",
                LocationX = -1,
                LocationY = -1,
                Url = GraphicUrl(request, environment, "lamp", graphic)
            };
            var colors = new[] { "warm-white", "cold-white", "purple", "red", "light-blue" };
            for (var i = 0; i < colors.Length; i++)
            {
                page.MenuItem.Add(new()
                {
                    Name = colors[i],
                    Url = Url(request, $"/ha/touch.xml?color={colors[i]}"),
                    TouchArea = new() { X1 = (ushort)(24 + i * 50), Y1 = 129, X2 = (ushort)(73 + i * 50), Y2 = 155 }
                });
            }
            page.MenuItem.Add(new()
            {
                Name = "Floor lamp",
                Url = Url(request, graphic == "unavailable" ? "/ha/touch.xml" : "/ha/touch.xml?toggle=true"),
                TouchArea = new() { X1 = 106, Y1 = 43, X2 = 192, Y2 = 123 }
            });
            AddSoftKey(page, "Dim", BrightnessNotify(request, "dim"), 1);
            AddSoftKey(page, "Brighter", BrightnessNotify(request, "brighter"), 2);
            AddSoftKey(page, "Refresh", Url(request, "/ha/touch.xml"), 3);
            AddSoftKey(page, "Exit", "Init:Services", 4);
            return CiscoXml.Result(page);
        });

    private static Task<IResult> ShowAirconAsync(
        HttpRequest request, HomeAssistantService ha, IWebHostEnvironment environment,
        bool toggle = false, double? temperature = null, int step = 0) =>
        HandleRequestAsync(request, ha, async () =>
        {
            var entity = ha.Slots.GetValueOrDefault("02");
            if (entity is null || entity.Domain != "climate") return Message(request, "Control unavailable", "The air conditioner is not enabled.");
            var states = await ha.GetStatesAsync();
            var state = states.FirstOrDefault(s => s.EntityId == entity.EntityId);
            if (step is not (-1 or 0 or 1)) return Results.BadRequest();
            if (temperature is not null || step != 0)
            {
                var target = temperature ?? NumericAttribute(state, "temperature", 24) + step;
                var min = NumericAttribute(state, "min_temp", 10);
                var max = NumericAttribute(state, "max_temp", 30);
                if (!double.IsFinite(target) || target < min || target > max) return Results.BadRequest();
                await ha.SetTemperatureAsync(entity, target);
                states = await ha.GetStatesAsync();
                state = states.FirstOrDefault(s => s.EntityId == entity.EntityId);
            }
            if (toggle && entity.CanControlState(state?.State))
            {
                states = await ha.ToggleAndWaitAsync(entity, state!.State!);
                state = states.FirstOrDefault(s => s.EntityId == entity.EntityId);
            }
            var graphic = state?.State switch
            {
                null or "unknown" or "unavailable" => "unavailable",
                "off" => "off",
                _ => "on"
            };
            var page = new CiscoIpPhoneGraphicFileMenu
            {
                Title = "Air conditioner menu",
                Prompt = FormattableString.Invariant($"{state?.State ?? "unavailable"} | Room {NumericAttribute(state, "current_temperature", 0):0.#}C | Set {NumericAttribute(state, "temperature", 24):0.#}C"),
                LocationX = -1,
                LocationY = -1,
                Url = GraphicUrl(request, environment, "aircon", graphic)
            };
            for (var i = 0; i < 5; i++)
            {
                var preset = 18 + i * 2;
                page.MenuItem.Add(new()
                {
                    Name = $"{preset}C",
                    Url = Url(request, $"/ha/aircon.xml?temperature={preset}"),
                    TouchArea = new() { X1 = (ushort)(24 + i * 50), Y1 = 129, X2 = (ushort)(73 + i * 50), Y2 = 155 }
                });
            }
            page.MenuItem.Add(new()
            {
                Name = "Air conditioner",
                Url = Url(request, "/ha/aircon.xml?toggle=true"),
                TouchArea = new() { X1 = 106, Y1 = 43, X2 = 192, Y2 = 123 }
            });
            AddSoftKey(page, "Cooler", Url(request, "/ha/aircon.xml?step=-1"), 1);
            AddSoftKey(page, "Warmer", Url(request, "/ha/aircon.xml?step=1"), 2);
            AddSoftKey(page, "Refresh", Url(request, "/ha/aircon.xml"), 3);
            AddSoftKey(page, "Exit", "Init:Services", 4);
            return CiscoXml.Result(page);
        });

    private static Task<IResult> ShowEntitiesAsync(
        HttpRequest request, HomeAssistantService ha, int page = 0, string? toggle = null) =>
        HandleRequestAsync(request, ha, async () =>
        {
            var entities = ha.Entities;
            if (entities.Count == 0)
                return Message(request, "No entities selected", "Configure HA_ENTITIES, then restart Beesly.");
            page = Math.Clamp(page, 0, (entities.Count - 1) / PageSize);
            var currentStates = await ha.GetStatesAsync();
            var selected = entities.FirstOrDefault(e => e.EntityId == toggle && e.CanToggle);
            var before = currentStates.FirstOrDefault(s => s.EntityId == selected?.EntityId);
            if (selected is not null && selected.CanControlState(before?.State))
                currentStates = await ha.ToggleAndWaitAsync(selected, before!.State!);
            var states = currentStates.ToDictionary(s => s.EntityId);
            var menu = new CiscoIpPhoneMenu { Title = "Home Assistant", Prompt = "Select to toggle" };
            foreach (var entity in entities.Skip(page * PageSize).Take(PageSize))
            {
                states.TryGetValue(entity.EntityId, out var state);
                var status = state?.State ?? "missing";
                var name = EntityName(entity, state);
                var suffix = $": {status}";
                var budget = Math.Max(1, 32 - suffix.Length);
                menu.MenuItem.Add(new CiscoIpPhoneMenuItemType
                {
                    Name = Truncate((name.Length > budget ? name[..budget] : name) + suffix),
                    Url = Url(request, entity.CanControlState(state?.State)
                        ? $"/ha.xml?page={page}&toggle={Uri.EscapeDataString(entity.EntityId)}"
                        : $"/ha.xml?page={page}")
                });
            }
            if (page > 0) menu.MenuItem.Add(new() { Name = "Previous page", Url = Url(request, $"/ha.xml?page={page - 1}") });
            if ((page + 1) * PageSize < entities.Count) menu.MenuItem.Add(new() { Name = "Next page", Url = Url(request, $"/ha.xml?page={page + 1}") });
            AddSoftKey(menu, "Select", "SoftKey:Select", 1);
            AddSoftKey(menu, "Refresh", Url(request, $"/ha.xml?page={page}"), 2);
            AddSoftKey(menu, "Back", Url(request, "/app.xml"), 3);
            AddSoftKey(menu, "Exit", "Init:Services", 4);
            return CiscoXml.Result(menu);
        });

    private static double NumericAttribute(HassState? state, string name, double fallback)
    {
        if (state?.AttributesJson is { } attributes && attributes.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return fallback;
    }

    private static IResult Message(HttpRequest request, string title, string text)
    {
        var screen = new CiscoIpPhoneText { Title = title, Prompt = "Home Assistant", Text = text };
        AddSoftKey(screen, "Retry", Url(request, "/ha.xml"), 1);
        AddSoftKey(screen, "Back", Url(request, "/app.xml"), 3);
        AddSoftKey(screen, "Exit", "Init:Services", 4);
        return CiscoXml.Result(screen);
    }

    private static async Task<IResult> HandleRequestAsync(HttpRequest request, HomeAssistantService ha, Func<Task<IResult>> render)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!ha.IsConfigured) return Message(request, "Token needed", "Configure HA_TOKEN or HA_TOKEN_FILE, then restart Beesly.");
        try
        {
            return await render();
        }
        catch (HomeAssistantApiCallException ex) when (ex.Code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return Message(request, "HA access denied", "Check the Home Assistant token and its permissions.");
        }
        catch (HomeAssistantApiCallException)
        {
            return Message(request, "HA request failed", "Check the entity ID and Home Assistant availability.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return Message(request, "HA access denied", "Check the Home Assistant token and its permissions.");
        }
        catch (HttpRequestException)
        {
            return Message(request, "HA request failed", "Check the server connection, entity ID and Home Assistant service availability.");
        }
        catch (OperationCanceledException)
        {
            return Message(request, "HA timed out", "Home Assistant did not respond. Try again shortly.");
        }
        catch (System.Text.Json.JsonException)
        {
            return Message(request, "Invalid HA response", "Home Assistant returned an unexpected response.");
        }
    }
}
