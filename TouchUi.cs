using CiscoIPPhone;
using CiscoIPPhoneApi;
using NetDaemon.Client.Exceptions;

namespace Beesly;

public static partial class HomeAssistantPhone
{
    private static string ViewPath(string view, int page = 0) => $"/ha/touch.xml?view={Uri.EscapeDataString(view)}&page={page}";

    private static IResult ShowTouchViews(HttpRequest request, AppSettings settings)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        var menu = new CiscoIpPhoneMenu
        {
            Title = "Home Assistant",
            Prompt = settings.Views.Count > 0 ? "Select a view" : "No touch views configured",
        };
        foreach (var (id, view) in settings.Views)
            menu.MenuItem.Add(new() { Name = view.Title, Url = Url(request, ViewPath(id)) });
        if (settings.Views.Count > 0) AddSoftKey(menu, "Select", "SoftKey:Select", 1);
        AddSoftKey(menu, "Exit", "Init:Services", 4);
        return CiscoXml.Result(menu);
    }

    private static IResult GetTouchGraphic(HttpRequest request, TouchRenderer renderer, string key)
    {
        if (renderer.Get(key) is not { } png) return Results.NotFound();
        request.HttpContext.Response.Headers.CacheControl = "public, max-age=600, immutable";
        return Results.Bytes(png, "image/png");
    }

    private static Task<IResult> ShowTouchViewAsync(
        HttpRequest request, HomeAssistantService ha, AppSettings settings, TouchRenderer renderer,
        string view, int page = 0, string? toggle = null, string? color = null, double? temperature = null, int step = 0)
    {
        if (!settings.Views.TryGetValue(view, out var config)) return Task.FromResult(Results.NotFound());
        var selected = config.Controls.FirstOrDefault(e => e.EntityId == toggle);
        // Validate the whole action before making any HA calls. Query strings cannot select arbitrary entities.
        if ((toggle is not null && selected is null) ||
            (color is not null && (!config.Swatch.Enable || !TouchRenderer.Colors.Contains(color))) ||
            (temperature is not null || step != 0) && config.Type != "aircon" || step is not (-1 or 0 or 1) ||
            new[] { toggle is not null, color is not null, temperature is not null, step != 0 }.Count(x => x) > 1)
            return Task.FromResult(Results.BadRequest());

        return HandleRequestAsync(request, ha, async () =>
        {
            var states = await ha.GetStatesAsync();
            if (selected is not null)
            {
                var before = states.FirstOrDefault(s => s.EntityId == selected.EntityId)?.State;
                if (selected.CanControlState(before)) states = await ha.ToggleAndWaitAsync(selected, before!);
            }
            if (color is not null)
            {
                await ha.SetColorAsync(config.Swatch.Entities, color);
                states = await ha.GetStatesAsync();
            }
            var climate = config.Type == "aircon" ? states.FirstOrDefault(s => s.EntityId == config.Entity!.EntityId) : null;
            if (temperature is not null || step != 0)
            {
                if (!config.Entity!.CanControlState(climate?.State)) return Results.Conflict();
                var target = temperature ?? NumericAttribute(climate, "temperature", 24) + step * NumericAttribute(climate, "target_temp_step", 1);
                if (!double.IsFinite(target) || target < NumericAttribute(climate, "min_temp", 10) || target > NumericAttribute(climate, "max_temp", 30))
                    return Results.BadRequest();
                await ha.SetTemperatureAsync(config.Entity, target);
                states = await ha.GetStatesAsync();
                climate = states.FirstOrDefault(s => s.EntityId == config.Entity.EntityId);
            }
            var pages = (config.Controls.Count + TouchRenderer.ControlsPerPage - 1) / TouchRenderer.ControlsPerPage;
            page = Math.Clamp(page, 0, pages - 1);
            var path = ViewPath(view, page);
            var controls = config.Controls.Skip(page * TouchRenderer.ControlsPerPage).Take(TouchRenderer.ControlsPerPage)
                .Select(entity =>
                {
                    var state = states.FirstOrDefault(s => s.EntityId == entity.EntityId);
                    return new TouchControl(EntityName(entity, state),
                        entity.CanControlState(state?.State) ? state!.State == "off" ? "off" : "on" : "unavailable",
                        entity.CanControlState(state?.State) ? $"&toggle={Uri.EscapeDataString(entity.EntityId)}" : "");
                }).ToArray();
            var layout = TouchRenderer.Layout(config, controls, page, pages);
            var screen = new CiscoIpPhoneGraphicFileMenu
            {
                Title = config.Title,
                Prompt = config.Type == "aircon"
                    ? FormattableString.Invariant($"{climate?.State ?? "unavailable"} | Room {NumericAttribute(climate, "current_temperature", 0):0.#} | Set {NumericAttribute(climate, "temperature", 24):0.#}")
                    : "Tap a control",
                LocationX = -1, LocationY = -1,
                Url = Url(request, $"/touch-ui/{renderer.Render(config.Title, layout)}.png"),
            };
            foreach (var button in layout)
                screen.MenuItem.Add(new()
                {
                    Name = button.Label.Replace('\n', ' '),
                    Url = button.Action == "Init:Services" ? button.Action
                        : Url(request, button.Page is { } targetPage ? ViewPath(view, targetPage) : path + button.Action),
                    TouchArea = new() { X1 = (ushort)button.X, Y1 = (ushort)button.Y, X2 = (ushort)(button.X + button.Width - 1), Y2 = (ushort)(button.Y + button.Height - 1) },
                });
            var brightnessTargets = BrightnessTargets(config);
            if (config.Type == "aircon")
            {
                AddSoftKey(screen, "Cooler", Url(request, path + "&step=-1"), 1);
                AddSoftKey(screen, "Warmer", Url(request, path + "&step=1"), 2);
            }
            else if (brightnessTargets.Count > 0)
            {
                var session = Guid.NewGuid().ToString("N");
                screen.AppId = "beesly-" + session;
                string Notify(string level, string phase)
                {
                    var target = $"{request.PathBase}/touch-ui/brightness/{level}?view={Uri.EscapeDataString(view)}&session={session}&phase={phase}".TrimStart('/');
                    return $"Notify:http:{request.Host.Host}:{request.Host.Port ?? 80}:{target}::";
                }
                screen.SoftKeyItem.Add(new() { Name = "Dim", UrlDown = Notify("dim", "press"), Url = Notify("dim", "release"), Position = 1 });
                screen.SoftKeyItem.Add(new() { Name = "Brighter", UrlDown = Notify("brighter", "press"), Url = Notify("brighter", "release"), Position = 2 });
                screen.OnAppFocusLost = Notify("stop", "cancel");
                screen.OnAppClosed = Notify("stop", "cancel");
            }
            AddSoftKey(screen, "Refresh", Url(request, path), 3);
            AddSoftKey(screen, "Exit", "Init:Services", 4);
            return CiscoXml.Result(screen);
        });
    }

    private static IReadOnlyList<HomeAssistantEntity> BrightnessTargets(TouchView view) =>
        view.Swatch.Enable ? view.Swatch.Entities : view.Controls.Where(e => e.Domain == "light").ToArray();

    private static async Task<IResult> AdjustViewBrightnessAsync(
        HttpRequest request, AppSettings settings, HomeAssistantService ha, BrightnessHoldService holds, ILogger<HomeAssistantService> logger,
        string view, string level, string phase = "step", Guid? session = null)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!settings.Views.TryGetValue(view, out var config)) return Results.NotFound();
        if (level is not ("dim" or "brighter" or "stop") || phase is not ("step" or "press" or "release" or "cancel") ||
            (level == "stop") != (phase == "cancel") || (phase != "step" && (session is null || session == Guid.Empty)))
            return Results.BadRequest();
        var targets = BrightnessTargets(config);
        if (targets.Count == 0) return Results.BadRequest();
        if (phase is "release" or "cancel")
        {
            holds.Release(session!.Value, view, phase == "cancel" ? null : level == "dim" ? -5 : 5);
            return Results.Text("OK", "text/plain");
        }
        if (!ha.IsConfigured) return Results.StatusCode(503);
        if (phase == "press")
        {
            holds.Press(session!.Value, view, level == "dim" ? -5 : 5, targets);
            return Results.Text("OK", "text/plain");
        }
        // Retain one-shot requests for already-open screens from before hold support.
        try
        {
            await ha.AdjustBrightnessAsync(targets, level == "dim" ? -20 : 20);
            return Results.Text("OK", "text/plain");
        }
        catch (Exception ex) when (ex is HttpRequestException or HomeAssistantApiCallException or OperationCanceledException)
        {
            logger.LogWarning("Brightness change failed for view {View}: {Reason}", view, ex.Message);
            return Results.StatusCode(502);
        }
    }
}
