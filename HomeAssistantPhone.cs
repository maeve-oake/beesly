using CiscoIPPhone;
using CiscoIPPhoneApi;
using NetDaemon.Client.HomeAssistant.Model;
using NetDaemon.Client.Exceptions;

namespace Beesly;

public static partial class HomeAssistantPhone
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
    public static void MapHomeAssistant(this WebApplication app)
    {
        app.MapGet("/ha.xml", ShowEntitiesAsync);
        app.MapGet("/ha/views.xml", ShowTouchViews);
        app.MapGet("/ha/touch.xml", ShowTouchViewAsync);
        app.MapGet("/touch-ui/{key}.png", GetTouchGraphic);
        app.MapPost("/touch-ui/brightness/{level}", AdjustViewBrightnessAsync);
    }

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
