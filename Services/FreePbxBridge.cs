using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Extensions;
using System.Reactive.Linq;
using System.Net.Sockets;

namespace Beesly;

public sealed class FreePbxBridge(AppSettings settings, HomeAssistantService ha,
    IHomeAssistantClient haClient, ILogger<FreePbxBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AmiPassword))
        {
            logger.LogInformation("FreePBX bridge disabled: no AMI_PASSWORD or AMI_PASSWORD_FILE configured.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("FreePBX bridge disconnected: {Reason}. Retrying in 5 seconds.", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = session.Token;
        using var socket = new TcpClient { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(settings.AmiHost!,
                settings.AmiPort, connectTimeout.Token);
        }
        await using var ami = new AmiConnection(socket.GetStream());
        var reader = ami.ReadAsync(ct);
        Task? worker = null;
        try
        {
            await ami.SendAsync("Login", new()
            {
                ["Username"] = settings.AmiUsername!,
                ["Secret"] = settings.AmiPassword,
                ["Events"] = "user"
            }, ct);
            logger.LogInformation("Connected to FreePBX AMI; {Count} BLF slots configured.", settings.Slots.Count);
            worker = ProcessEventsAsync(ami, ct);
            var finished = await Task.WhenAny(reader, worker);
            await finished;
        }
        finally
        {
            session.Cancel();
            // The first failure was propagated above; drain both tasks before disposing the socket.
            try { await reader; } catch (Exception) { }
            if (worker is not null)
            {
                try { await worker; } catch (Exception) { }
            }
        }
    }

    private async Task ProcessEventsAsync(AmiConnection ami, CancellationToken ct)
    {
        // One worker orders button presses and state publication; no overlapping HA toggles.
        var url = settings.HaUrl;
        await using var connection = await haClient.ConnectAsync(url.Host, url.Port, url.Scheme == "https",
            settings.HaToken, ct)
            ?? throw new IOException("Cannot connect to Home Assistant WebSocket.");
        var changes = await connection.SubscribeToHomeAssistantEventsAsync("state_changed", ct);
        using var subscription = changes.Subscribe(change =>
        {
            var state = change.ToStateChangedEvent();
            if (state is not null)
                ami.Events.Writer.TryWrite(new()
                {
                    ["Event"] = "HAState",
                    ["Entity"] = state.EntityId,
                    ["State"] = state.NewState?.State ?? "unavailable"
                });
        });
        var snapshot = await connection.GetStatesAsync(ct)
            ?? throw new IOException("Missing HA state snapshot.");
        var states = snapshot.ToDictionary(s => s.EntityId, s => s.State);
        var disconnected = connection.WaitForConnectionToCloseAsync(ct);
        logger.LogInformation("Subscribed to HA state changes over WebSocket.");
        var published = new Dictionary<string, string>();
        while (!ct.IsCancellationRequested)
        {
            while (ami.Events.Reader.TryRead(out var message))
            {
                if (message.GetValueOrDefault("Event") == "HAState")
                {
                    states[message["Entity"]] = message["State"];
                    continue;
                }
                if (message.GetValueOrDefault("Event") != "UserEvent" ||
                    message.GetValueOrDefault("UserEvent") != "BeeslyToggle") continue;
                var slot = message.GetValueOrDefault("Slot") ?? "";
                if (!settings.Slots.TryGetValue(slot, out var entity))
                {
                    logger.LogWarning("Ignoring unconfigured Beesly slot {Slot}.", slot);
                    continue;
                }
                try
                {
                    await ha.ToggleAsync(entity);
                    logger.LogInformation("Button {Slot}: toggled {Entity}.", slot, entity.EntityId);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Button {Slot} failed: {Reason}", slot, ex.Message);
                }
            }

            foreach (var (slot, entity) in settings.Slots)
            {
                var state = states.GetValueOrDefault(entity.EntityId) switch
                {
                    null or "unknown" or "unavailable" => "UNAVAILABLE",
                    "off" => "NOT_INUSE",
                    "on" => "INUSE",
                    _ when entity.Domain == "climate" => "INUSE",
                    _ => "UNAVAILABLE"
                };
                if (published.GetValueOrDefault(slot) == state) continue;
                await PublishAsync(ami, slot, state, ct);
                published[slot] = state;
                logger.LogInformation("BLF {Slot}: {State} ({Entity}).", slot, state, entity.EntityId);
            }
            // Configuration is fixed at startup; only events or disconnects wake the worker.
            var ready = ami.Events.Reader.WaitToReadAsync(ct).AsTask();
            if (await Task.WhenAny(ready, disconnected) == disconnected)
                throw new IOException("Home Assistant WebSocket disconnected.");
            if (!await ready)
                throw new IOException("AMI connection closed.");
        }
    }

    private static Task PublishAsync(AmiConnection ami, string slot, string state, CancellationToken ct) =>
        ami.SendAsync("Setvar", new() { ["Variable"] = $"DEVICE_STATE(Custom:beesly_{slot})", ["Value"] = state }, ct);
}
