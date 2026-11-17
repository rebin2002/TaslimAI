using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Taslim.Api.Domain;

namespace Taslim.Api.Presentations;

public sealed class PresentationOutputIntegrityException(string message, Exception? innerException = null) : Exception(message, innerException);

public static class PresentationOutputIntegrityValidator
{
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string SlideMasterRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster";
    private const string SlideRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
    private const string SlideLayoutRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout";

    // ECMA-376 Part 1 pairs each theme format style list with a fixed-length content model: CT_FillStyleList,
    // CT_LineStyleList, CT_EffectStyleList and CT_BackgroundFillStyleList all declare minOccurs=maxOccurs=3, so
    // a theme shipping any other number of entries is schema-invalid.
    private const int FormatSchemeEntryCount = 3;

    private static readonly string[] FormatFillEntries = ["solidFill", "gradFill", "blipFill", "pattFill", "grpFill", "noFill"];

    private static readonly IReadOnlyList<(string List, IReadOnlyList<string> AllowedEntries)> FormatSchemeLists =
    [
        ("fillStyleLst", FormatFillEntries),
        ("lnStyleLst", new[] { "ln" }),
        ("effectStyleLst", new[] { "effectStyle" }),
        ("bgFillStyleLst", FormatFillEntries),
    ];

    private static readonly string[] RequiredParts =
    [
        "[Content_Types].xml",
        "_rels/.rels",
        "ppt/presentation.xml",
        "ppt/_rels/presentation.xml.rels",
        "ppt/theme/theme1.xml",
        "ppt/slideMasters/slideMaster1.xml",
        "ppt/slideMasters/_rels/slideMaster1.xml.rels",
        "ppt/slideLayouts/slideLayout1.xml",
        "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
    ];

    public static void Validate(RenderedPresentation rendered)
    {
        if (!string.Equals(rendered.RepresentationType, AssetRepresentationTypes.Pptx, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rendered.ContentType, "application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase)
            || !rendered.FileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            throw new PresentationOutputIntegrityException("The presentation output metadata is invalid.");

        if (rendered.Content.Length == 0)
            throw new PresentationOutputIntegrityException("The presentation output is empty.");

        try
        {
            using var stream = new MemoryStream(rendered.Content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = archive.Entries;
            var names = entries.Select(entry => entry.FullName).ToArray();
            if (names.Length != names.Distinct(StringComparer.Ordinal).Count())
                throw new PresentationOutputIntegrityException("The presentation package contains duplicate parts.");
            if (names.Any(name => name.StartsWith("/", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)))
                throw new PresentationOutputIntegrityException("The presentation package contains an unsafe part path.");

            foreach (var part in RequiredParts)
                _ = archive.GetEntry(part) ?? throw new PresentationOutputIntegrityException($"The presentation package is missing required part '{part}'.");

            RequireRoot(ParseRequiredXml(archive, "[Content_Types].xml"), "Types", ContentTypesNamespace, "[Content_Types].xml");
            RequireRoot(ParseRequiredXml(archive, "_rels/.rels"), "Relationships", RelationshipsNamespace, "_rels/.rels");
            var themeRoot = RequireRoot(ParseRequiredXml(archive, "ppt/theme/theme1.xml"), "theme", DrawingNamespace, "ppt/theme/theme1.xml");
            RequireFormatScheme(themeRoot);
            var slideMasterRoot = RequireRoot(ParseRequiredXml(archive, "ppt/slideMasters/slideMaster1.xml"), "sldMaster", PresentationNamespace, "ppt/slideMasters/slideMaster1.xml");
            var slideMasterRelationships = RequireRoot(ParseRequiredXml(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels"), "Relationships", RelationshipsNamespace, "ppt/slideMasters/_rels/slideMaster1.xml.rels");
            RequireRoot(ParseRequiredXml(archive, "ppt/slideLayouts/slideLayout1.xml"), "sldLayout", PresentationNamespace, "ppt/slideLayouts/slideLayout1.xml");
            RequireRoot(ParseRequiredXml(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels"), "Relationships", RelationshipsNamespace, "ppt/slideLayouts/_rels/slideLayout1.xml.rels");

            var masterLayoutReference = slideMasterRoot
                .Element(XName.Get("sldLayoutIdLst", PresentationNamespace))?
                .Elements(XName.Get("sldLayoutId", PresentationNamespace))
                .SingleOrDefault()?
                .Attribute(XName.Get("id", OfficeRelationshipNamespace))?.Value;
            RequireRelationship(slideMasterRelationships, masterLayoutReference, SlideLayoutRelationshipType, "../slideLayouts/slideLayout1.xml", "ppt/slideMasters/_rels/slideMaster1.xml.rels");

            var presentationRoot = RequireRoot(ParseRequiredXml(archive, "ppt/presentation.xml"), "presentation", PresentationNamespace, "ppt/presentation.xml");
            var presentationRelationships = RequireRoot(ParseRequiredXml(archive, "ppt/_rels/presentation.xml.rels"), "Relationships", RelationshipsNamespace, "ppt/_rels/presentation.xml.rels");
            var masterReference = presentationRoot
                .Element(XName.Get("sldMasterIdLst", PresentationNamespace))?
                .Elements(XName.Get("sldMasterId", PresentationNamespace))
                .SingleOrDefault()?
                .Attribute(XName.Get("id", OfficeRelationshipNamespace))?.Value;
            RequireRelationship(presentationRelationships, masterReference, SlideMasterRelationshipType, "slideMasters/slideMaster1.xml", "ppt/_rels/presentation.xml.rels");
            var slideIds = presentationRoot
                .Element(XName.Get("sldIdLst", PresentationNamespace))?
                .Elements(XName.Get("sldId", PresentationNamespace))
                .ToArray() ?? [];
            if (slideIds.Length == 0)
                throw new PresentationOutputIntegrityException("The presentation package does not contain any slides.");

            var size = presentationRoot.Element(XName.Get("sldSz", PresentationNamespace));
            if (size is null || !TryPositiveLong(size.Attribute("cx")?.Value, out _) || !TryPositiveLong(size.Attribute("cy")?.Value, out _))
                throw new PresentationOutputIntegrityException("The presentation package has invalid slide dimensions.");

            var slideNumbers = entries
                .Select(entry => TryGetSlideNumber(entry.FullName, out var number) ? number : (int?)null)
                .Where(number => number.HasValue)
                .Select(number => number!.Value)
                .OrderBy(number => number)
                .ToArray();
            if (!slideNumbers.SequenceEqual(Enumerable.Range(1, slideIds.Length)))
                throw new PresentationOutputIntegrityException("The presentation package has an incomplete or non-contiguous slide set.");

            foreach (var number in slideNumbers)
            {
                RequireRoot(ParseRequiredXml(archive, $"ppt/slides/slide{number}.xml"), "sld", PresentationNamespace, $"ppt/slides/slide{number}.xml");
                var slideRelationships = RequireRoot(ParseRequiredXml(archive, $"ppt/slides/_rels/slide{number}.xml.rels"), "Relationships", RelationshipsNamespace, $"ppt/slides/_rels/slide{number}.xml.rels");
                RequireRelationship(slideRelationships, "rId1", SlideLayoutRelationshipType, "../slideLayouts/slideLayout1.xml", $"ppt/slides/_rels/slide{number}.xml.rels");
                var slideReference = slideIds[number - 1].Attribute(XName.Get("id", OfficeRelationshipNamespace))?.Value;
                RequireRelationship(presentationRelationships, slideReference, SlideRelationshipType, $"slides/slide{number}.xml", "ppt/_rels/presentation.xml.rels");
            }
        }
        catch (PresentationOutputIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException or ArgumentException)
        {
            throw new PresentationOutputIntegrityException("The presentation package could not be read safely.", exception);
        }
    }

    private static XDocument ParseRequiredXml(ZipArchive archive, string part)
    {
        var entry = archive.GetEntry(part) ?? throw new PresentationOutputIntegrityException($"The presentation package is missing required part '{part}'.");
        try
        {
            using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or IOException)
        {
            throw new PresentationOutputIntegrityException($"The presentation package contains invalid XML in '{part}'.", exception);
        }
    }

    // A theme with empty or short format style lists is rejected by PowerPoint as damaged content even though
    // every part and relationship still resolves, so the relationship graph alone cannot certify a package as
    // exportable. Validate the content model that the format scheme entries must satisfy.
    private static void RequireFormatScheme(XElement theme)
    {
        var elements = theme.Element(XName.Get("themeElements", DrawingNamespace))
            ?? throw new PresentationOutputIntegrityException("The presentation theme is missing its elements.");
        var scheme = elements.Element(XName.Get("fmtScheme", DrawingNamespace))
            ?? throw new PresentationOutputIntegrityException("The presentation theme is missing its format scheme.");

        foreach (var (listLocalName, allowedEntries) in FormatSchemeLists)
        {
            var list = scheme.Element(XName.Get(listLocalName, DrawingNamespace))
                ?? throw new PresentationOutputIntegrityException($"The presentation theme format scheme is missing '{listLocalName}'.");
            var entries = list.Elements().ToArray();
            if (entries.Length != FormatSchemeEntryCount)
                throw new PresentationOutputIntegrityException($"The presentation theme format scheme '{listLocalName}' must declare exactly {FormatSchemeEntryCount} entries.");
            if (entries.Any(entry => entry.Name.NamespaceName != DrawingNamespace || !allowedEntries.Contains(entry.Name.LocalName)))
                throw new PresentationOutputIntegrityException($"The presentation theme format scheme '{listLocalName}' declares an unsupported entry.");
        }

        // CT_EffectStyleItem requires either an ordered effect list or an effect graph, so an effect style
        // carrying neither child describes no style at all.
        foreach (var effectStyle in scheme.Element(XName.Get("effectStyleLst", DrawingNamespace))!.Elements())
            if (effectStyle.Element(XName.Get("effectLst", DrawingNamespace)) is null
                && effectStyle.Element(XName.Get("effectDag", DrawingNamespace)) is null)
                throw new PresentationOutputIntegrityException("The presentation theme declares an effect style without an effect list.");
    }

    private static XElement RequireRoot(XDocument document, string localName, string namespaceName, string part)
    {
        var root = document.Root;
        if (root is null || root.Name != XName.Get(localName, namespaceName))
            throw new PresentationOutputIntegrityException($"The presentation package has an invalid root in '{part}'.");
        return root;
    }

    private static void RequireRelationship(XElement relationships, string? id, string type, string target, string part)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !relationships.Elements(XName.Get("Relationship", RelationshipsNamespace)).Any(item =>
                string.Equals(item.Attribute("Id")?.Value, id, StringComparison.Ordinal)
                && string.Equals(item.Attribute("Type")?.Value, type, StringComparison.Ordinal)
                && string.Equals(item.Attribute("Target")?.Value, target, StringComparison.Ordinal)))
            throw new PresentationOutputIntegrityException($"The presentation package is missing relationship '{id ?? "<missing>"}' to '{target}' in '{part}'.");
    }

    private static bool TryGetSlideNumber(string name, out int number)
    {
        const string prefix = "ppt/slides/slide";
        number = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".xml", StringComparison.Ordinal)) return false;
        var value = name[prefix.Length..^4];
        return int.TryParse(value, out number) && number > 0;
    }

    private static bool TryPositiveLong(string? value, out long result) => long.TryParse(value, out result) && result > 0;
}
