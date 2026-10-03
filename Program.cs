using CiscoIPPhone;
using CiscoIPPhoneApi;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5220");

var app = builder.Build();

app.MapGet("/app.xml", () =>
{
    var menu = new CiscoIpPhoneMenu
    {
        Title = "Site Directory",
        Prompt = "Select a destination",
        MenuItem =
        {
            new CiscoIpPhoneMenuItemType { Name = "TOUCH.XML", Url = "http://elster.lan.ci:8000/touch.xml" },
        },
        SoftKeyItem =
        {
            new CiscoIpPhoneSoftKeyType { Name = "Select", Url = "SoftKey:Select", Position = 1 },
            new CiscoIpPhoneSoftKeyType { Name = "Exit",   Url = "SoftKey:Exit",   Position = 4 },
        },
    };

    return CiscoXml.Result(menu);

});

app.Run();
