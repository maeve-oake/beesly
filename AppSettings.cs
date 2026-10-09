using System.Net;
using System.Net.Sockets;
using System.Text.Json;

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
    public IReadOnlyDictionary<string, TouchView> Views { get; private init; } = new Dictionary<string, TouchView>();
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
            Views = ReadViews(),
            Slots = ReadSlots(),
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new EntityConverter() },
    };

    private static T ReadJson<T>(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        var file = Environment.GetEnvironmentVariable(variable + "_FILE");
        if (value is not null && file is not null)
            throw new InvalidOperationException($"Set either {variable} or {variable}_FILE, not both.");
        return JsonSerializer.Deserialize<T>(file is null ? value ?? fallback : File.ReadAllText(file), JsonOptions)
            ?? throw new InvalidOperationException($"{variable} must not be null.");
    }

    private static List<HomeAssistantEntity> ReadEntities()
    {
        var entities = ReadJson<List<HomeAssistantEntity>>("HA_ENTITIES", "[]");
        foreach (var entity in entities) HomeAssistantEntity.Validate(entity);
        if (entities.Select(e => e.EntityId).Distinct().Count() != entities.Count)
            throw new InvalidOperationException("HA_ENTITIES contains duplicate entity IDs.");
        return entities;
    }

    private static Dictionary<string, TouchView> ReadViews()
    {
        var views = ReadJson<Dictionary<string, TouchView>>("UI_VIEWS", "{}");
        foreach (var (id, view) in views)
        {
            if (view is null) throw new InvalidOperationException($"UI view '{id}' must be an object.");
            view.Validate(id);
        }
        return views;
    }

    private static Dictionary<string, HomeAssistantEntity> ReadSlots()
    {
        var mappings = ReadJson<Dictionary<string, string>>("AMI_SLOTS", "{}");
        var slots = new Dictionary<string, HomeAssistantEntity>();
        foreach (var (slot, id) in mappings)
        {
            var entity = new HomeAssistantEntity { EntityId = id };
            HomeAssistantEntity.Validate(entity);
            if (!int.TryParse(slot, out var number) || number is < 1 or > 24 || !entity.CanToggle)
                throw new InvalidOperationException($"AMI_SLOTS entry {slot} must map a slot from 01–24 to a controllable HA entity.");
            if (!slots.TryAdd(number.ToString("D2"), entity))
                throw new InvalidOperationException($"AMI_SLOTS contains duplicate slot {number:D2}.");
        }
        return slots;
    }
}
