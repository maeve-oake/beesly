using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Beesly;

public sealed class TouchView
{
    public string Title { get; init; } = "";
    public string Type { get; init; } = "";
    public HomeAssistantEntity? Entity { get; init; }
    public IReadOnlyList<HomeAssistantEntity> Entities { get; init; } = [];
    public TouchSwatch Swatch { get; init; } = new();
    [JsonIgnore]
    public IReadOnlyList<HomeAssistantEntity> Controls => Type == "multiple" ? Entities : [Entity!];

    public void Validate(string id)
    {
        void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException($"UI view '{id}': {message}");
        }
        Require(Regex.IsMatch(id, @"^[a-z0-9][a-z0-9_-]*$"), "invalid view ID.");
        Require(!string.IsNullOrWhiteSpace(Title), "title is required.");
        Require(Type is "lamp" or "aircon" or "multiple", "type must be lamp, aircon or multiple.");
        Require(Type == "multiple" ? Entity is null && Entities is { Count: > 0 } : Entity is not null && Entities is { Count: 0 },
            "use entity for lamp/aircon, or a non-empty entities list for multiple.");
        foreach (var entity in Controls) HomeAssistantEntity.Validate(entity);
        Require(Controls.All(e => e.CanToggle), "controls must be controllable entities.");
        Require(Controls.Select(e => e.EntityId).Distinct().Count() == Controls.Count, "duplicate controls.");
        Require(Type != "lamp" || Entity!.Domain == "light", "lamp requires a light entity.");
        Require(Type != "aircon" || Entity!.Domain == "climate", "aircon requires a climate entity.");
        Require(Swatch is not null, "swatch must be an object.");
        Require(Swatch!.Entities is not null, "swatch entities must be a list.");
        foreach (var entity in Swatch.Entities!) HomeAssistantEntity.Validate(entity);
        Require(!Swatch.Enable || Swatch.Entities is { Count: > 0 }, "enabled swatch requires explicit target entities.");
        Require(Swatch.Entities.All(e => e.CanToggle && e.Domain == "light"), "swatch targets must be lights.");
        Require(Swatch.Entities.Select(e => e.EntityId).Distinct().Count() == Swatch.Entities.Count, "duplicate swatch targets.");
    }
}

public sealed class TouchSwatch
{
    public bool Enable { get; init; }
    public IReadOnlyList<HomeAssistantEntity> Entities { get; init; } = [];
}

// Main-menu and touch-view entries accept an entity ID or a named entity object.
public sealed class EntityConverter : JsonConverter<HomeAssistantEntity>
{
    public override HomeAssistantEntity Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new HomeAssistantEntity { EntityId = reader.GetString()! };
        return JsonSerializer.Deserialize<HomeAssistantEntity>(ref reader, ObjectOptions)
            ?? throw new JsonException("Expected an entity ID or object.");
    }

    private static readonly JsonSerializerOptions ObjectOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public override void Write(Utf8JsonWriter writer, HomeAssistantEntity value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new { entityId = value.EntityId, name = value.Name, allowToggle = value.AllowToggle }, options);
}
