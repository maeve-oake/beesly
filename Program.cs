using CiscoIPPhone;
using CiscoIPPhoneApi;
using Beesly;
using NetDaemon.Client.Extensions;
using NetDaemon.Client.Settings;

DotNetEnv.Env.NoClobber().Load(".env");
var settings = AppSettings.Load();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = "Production",
});
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Logging:LogLevel:Default"] = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information",
    ["Logging:LogLevel:Microsoft.AspNetCore"] = Environment.GetEnvironmentVariable("FRAMEWORK_LOG_LEVEL") ?? "Warning",
    ["AllowedHosts"] = Environment.GetEnvironmentVariable("ALLOWED_HOSTS") ?? "*",
});
builder.WebHost.UseUrls($"http://{settings.ListenAddress}:{settings.Port}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<TouchRenderer>();
builder.Services.Configure<HomeAssistantSettings>(client =>
{
    client.Host = settings.HaUrl.Host;
    client.Port = settings.HaUrl.Port;
    client.Ssl = settings.HaUrl.Scheme == "https";
    client.Token = settings.HaToken;
});
builder.Services.AddHomeAssistantClient();
builder.Services.AddSingleton<HomeAssistantService>();
builder.Services.AddSingleton<BrightnessHoldService>();
builder.Services.AddHostedService(services => services.GetRequiredService<BrightnessHoldService>());
builder.Services.AddHostedService<FreePbxBridge>();

var app = builder.Build();

app.MapGet("/app.xml", (HttpRequest request) =>
{
    var menu = new CiscoIpPhoneMenu
    {
        Title = "Site Directory",
        Prompt = "Select a destination",
        MenuItem =
        {
            new CiscoIpPhoneMenuItemType { Name = "Home Assistant", Url = HomeAssistantPhone.Url(request, "/ha.xml") },
        },
        SoftKeyItem =
        {
            new CiscoIpPhoneSoftKeyType { Name = "Select", Url = "SoftKey:Select", Position = 1 },
            new CiscoIpPhoneSoftKeyType { Name = "Exit",   Url = "Init:Services",   Position = 4 },
        },
    };

    foreach (var (id, view) in settings.Views)
        menu.MenuItem.Add(new() { Name = view.Title, Url = HomeAssistantPhone.Url(request, $"/ha/touch.xml?view={Uri.EscapeDataString(id)}") });
    return CiscoXml.Result(menu);
});

app.MapHomeAssistant();
app.Run();
