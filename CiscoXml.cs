using System.Xml;
using System.Xml.Serialization;

namespace CiscoIPPhoneApi;

public static class CiscoXml
{
    public static string Serialize<T>(T obj)
    {
        var ns = new XmlSerializerNamespaces();
        ns.Add("", ""); // no xmlns:xsi / xmlns:xsd

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true };
        using var sw = new StringWriter();
        using (var xw = XmlWriter.Create(sw, settings))
            new XmlSerializer(typeof(T)).Serialize(xw, obj, ns);
        return sw.ToString();
    }

    public static IResult Result<T>(T obj) => Results.Content(Serialize(obj), "text/xml");
}
