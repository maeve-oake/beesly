using System.Net.Http.Headers;
using CiscoIPPhone;
using CiscoIPPhoneApi;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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

