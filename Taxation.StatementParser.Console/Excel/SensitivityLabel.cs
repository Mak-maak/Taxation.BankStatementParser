using System.Globalization;
using System.IO.Packaging;
using System.Xml.Linq;

namespace Taxation.StatementParser.Console.Excel;

/// <summary>
/// Applies a Microsoft Information Protection (MIP) sensitivity label to a saved .xlsx file.
///
/// The coloured "Public / Confidential" badge Office displays is driven by a sensitivity label,
/// stored as a set of <c>MSIP_Label_&lt;GUID&gt;_*</c> custom document properties inside
/// <c>docProps/custom.xml</c> of the OpenXML package - it is NOT the built-in "Status" property.
/// ClosedXML cannot write arbitrary custom properties, so we inject them directly into the package
/// after it has been saved.
///
/// The label GUID and display name are defined by each organisation's Microsoft Purview tenant.
/// The defaults below describe a "Confidential" label; adjust <see cref="ConfidentialLabelId"/> and
/// <see cref="ConfidentialLabelName"/> to match your tenant's published label if they differ.
/// </summary>
internal static class SensitivityLabel
{
    /// <summary>The GUID of the organisation's "Confidential" sensitivity label.</summary>
    internal const string ConfidentialLabelId = "1e5e3f43-8a5a-4f1c-9c2b-000000000001";

    /// <summary>The display name of the "Confidential" label (shown on the Office badge).</summary>
    internal const string ConfidentialLabelName = "Confidential";

    private const string CustomPropsUri = "/docProps/custom.xml";
    private const string CustomPropsContentType =
        "application/vnd.openxmlformats-officedocument.custom-properties+xml";
    private const string CustomPropsRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties";

    private static readonly XNamespace CustomNs =
        "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
    private static readonly XNamespace VtNs =
        "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

    /// <summary>
    /// Writes the MSIP_Label properties that mark <paramref name="xlsxPath"/> as Confidential.
    /// </summary>
    internal static void ApplyConfidential(string xlsxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xlsxPath);

        var properties = BuildConfidentialProperties();

        using Package package = Package.Open(xlsxPath, FileMode.Open, FileAccess.ReadWrite);
        var partUri = new Uri(CustomPropsUri, UriKind.Relative);

        PackagePart part;
        if (package.PartExists(partUri))
        {
            part = package.GetPart(partUri);
        }
        else
        {
            part = package.CreatePart(partUri, CustomPropsContentType);
            package.CreateRelationship(partUri, TargetMode.Internal, CustomPropsRelationship);
        }

        XDocument document = BuildCustomPropertiesXml(properties);

        using Stream stream = part.GetStream(FileMode.Create, FileAccess.Write);
        document.Save(stream);
    }

    private static IReadOnlyDictionary<string, string> BuildConfidentialProperties()
    {
        string prefix = $"MSIP_Label_{ConfidentialLabelId}";
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{prefix}_Enabled"] = "true",
            [$"{prefix}_SetDate"] = timestamp,
            [$"{prefix}_Method"] = "Standard",
            [$"{prefix}_Name"] = ConfidentialLabelName,
            [$"{prefix}_SiteId"] = "00000000-0000-0000-0000-000000000000",
            [$"{prefix}_ActionId"] = Guid.NewGuid().ToString(),
            [$"{prefix}_ContentBits"] = "0",
        };
    }

    private static XDocument BuildCustomPropertiesXml(IReadOnlyDictionary<string, string> properties)
    {
        var root = new XElement(CustomNs + "Properties",
            new XAttribute(XNamespace.Xmlns + "vt", VtNs.NamespaceName));

        // fmtid is the well-known GUID Office uses for custom document properties; pids start at 2.
        const string formatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";
        int pid = 2;

        foreach (KeyValuePair<string, string> property in properties)
        {
            root.Add(new XElement(CustomNs + "property",
                new XAttribute("fmtid", formatId),
                new XAttribute("pid", pid.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("name", property.Key),
                new XElement(VtNs + "lpwstr", property.Value)));
            pid++;
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }
}
