using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beesly;

public sealed class AppSettings
{
    public string ListenAddress { get; private init; } = "";
    public int Port { get; private init; }
    public Uri HaUrl { get; }
    public string HaToken { get; private init; } = "";
    public string? AmiHost { get; private init; }
    public int AmiPort { get; private init; }
    public string? AmiUsername { get; private init; }
    public string AmiPassword { get; private init; } = "";
    public IReadOnlyList<HomeAssistantEntity> Entities { get; private init; } = [];
    public IReadOnlyDictionary<string, HomeAssistantEntity> Slots { get; private init; } = new Dictionary<string, HomeAssistantEntity>();

    private AppSettings(Uri haUrl) => HaUrl = haUrl;

    public static AppSettings Load()
    {
        var address = Environment.GetEnvironmentVariable("LISTEN_ADDRESS") ?? "0.0.0.0";
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("LISTEN_ADDRESS must be an IPv4 address.");

        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("HA_URL"), UriKind.Absolute, out var haUrl) ||
            haUrl.Scheme is not ("http" or "https") || haUrl.AbsolutePath != "/")
            throw new InvalidOperationException("HA_URL must be set to an HTTP(S) instance URL without a path.");

        var amiPassword = ReadSecret("AMI_PASSWORD");
        var amiHost = Environment.GetEnvironmentVariable("AMI_HOST");
        var amiUsername = Environment.GetEnvironmentVariable("AMI_USERNAME");
        if (!string.IsNullOrWhiteSpace(amiPassword) &&
            (string.IsNullOrWhiteSpace(amiHost) || string.IsNullOrWhiteSpace(amiUsername)))
            throw new InvalidOperationException("AMI_HOST and AMI_USERNAME must be set when an AMI password is configured.");

        var entities = ReadEntities();
        return new AppSettings(haUrl)
        {
            ListenAddress = address,
            Port = ReadPort("PORT", 6971),
            HaToken = ReadSecret("HA_TOKEN"),
            AmiHost = amiHost,
            AmiPort = ReadPort("AMI_PORT", 5038),
            AmiUsername = amiUsername,
            AmiPassword = amiPassword,
            Entities = entities,
            Slots = ReadSlots(entities),
        };
    }

    private static int ReadPort(string variable, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (value is null) return fallback;
        if (int.TryParse(value, out var port) && port is >= 1 and <= 65535) return port;
        throw new InvalidOperationException($"{variable} must be between 1 and 65535.");
    }

    private static string ReadSecret(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        var path = Environment.GetEnvironmentVariable(variable + "_FILE");
        if (string.IsNullOrEmpty(path)) return value ?? "";
        if (value is not null)
            throw new InvalidOperationException($"Set either {variable} or {variable}_FILE, not both.");

        var secret = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException($"{variable}_FILE must contain a non-empty secret.");
        return secret;
    }

    private static List<HomeAssistantEntity> ReadEntities()
    {
        var entries = JsonSerializer.Deserialize<JsonElement[]>(Environment.GetEnvironmentVariable("HA_ENTITIES") ?? "[]")
            ?? throw new InvalidOperationException("HA_ENTITIES must be a JSON array.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var entities = new List<HomeAssistantEntity>();
        var ids = new HashSet<string>();

        foreach (var entry in entries)
        {
            var entity = entry.ValueKind switch
            {
                JsonValueKind.String => new HomeAssistantEntity { EntityId = entry.GetString()! },
                JsonValueKind.Object => entry.Deserialize<HomeAssistantEntity>(options)!,
                _ => throw new InvalidOperationException("HA_ENTITIES entries must be entity IDs or objects."),
            };
            if (string.IsNullOrEmpty(entity.EntityId) || !Regex.IsMatch(entity.EntityId, @"^[a-z_]+\.[a-z0-9_]+$") || !ids.Add(entity.EntityId))
                throw new InvalidOperationException("HA_ENTITIES must contain unique, valid entity IDs.");
            entities.Add(entity);
        }
        return entities;
    }

    private static Dictionary<string, HomeAssistantEntity> ReadSlots(IReadOnlyList<HomeAssistantEntity> entities)
    {
        var mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(Environment.GetEnvironmentVariable("AMI_SLOTS") ?? "{}")
            ?? throw new InvalidOperationException("AMI_SLOTS must be a JSON object.");
        var slots = new Dictionary<string, HomeAssistantEntity>();
        foreach (var (slot, id) in mappings)
        {
            var entity = entities.FirstOrDefault(entity => entity.EntityId == id);
            if (!int.TryParse(slot, out var number) || number is < 1 or > 24 || entity is null || !entity.CanToggle)
                throw new InvalidOperationException($"AMI_SLOTS entry {slot} must map a slot from 01–24 to an enabled, controllable HA entity.");
            if (!slots.TryAdd(number.ToString("D2"), entity))
                throw new InvalidOperationException($"AMI_SLOTS contains duplicate slot {number:D2}.");
        }
        return slots;
    }
}
