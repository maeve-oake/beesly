namespace Beesly;

public sealed class HomeAssistantEntity
{
    public string EntityId { get; init; } = "";
    public string? Name { get; init; }
    public bool AllowToggle { get; init; } = true;
    public string Domain => EntityId.Split('.')[0];
    public bool CanToggle => AllowToggle && Domain is "light" or "switch" or "input_boolean" or "fan" or "climate";

    public bool CanControlState(string? state)
    {
        if (!CanToggle || state is null or "unknown" or "unavailable") return false;
        return Domain == "climate" || state is "on" or "off";
    }
}
