using System.Xml;
using System.Xml.Serialization;

namespace CiscoIPPhoneApi;

public static class CiscoXml
{
    public static string Serialize<T>(T obj)
    {
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add("", ""); // no xmlns:xsi / xmlns:xsd

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true };
        using var text = new StringWriter();
        using (var writer = XmlWriter.Create(text, settings))
            new XmlSerializer(typeof(T)).Serialize(writer, obj, namespaces);
        return text.ToString();
    }

    public static IResult Result<T>(T obj) => Results.Content(Serialize(obj), "text/xml");
}
