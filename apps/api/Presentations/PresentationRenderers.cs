using System.IO.Compression;
using System.Security;
using System.Text;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Presentations;

public sealed record RenderedPresentation(string RepresentationType, string FileName, string ContentType, byte[] Content);

public interface IPresentationRenderer
{
    RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options);
}

public sealed class PresentationRenderer : IPresentationRenderer
{
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const int SlideWidth = 12_192_000;
    private const int SlideHeight = 6_858_000;
    private int shapeId;

    public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options)
    {
        if (draft.Slides.Count == 0) throw new InvalidOperationException("A presentation must contain at least one slide.");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Write(archive, "[Content_Types].xml", ContentTypes(draft.Slides.Count));
            Write(archive, "_rels/.rels", RootRelationships());
            Write(archive, "ppt/presentation.xml", Presentation(draft.Slides.Count));
            Write(archive, "ppt/_rels/presentation.xml.rels", PresentationRelationships(draft.Slides.Count));
            Write(archive, "ppt/theme/theme1.xml", Theme());
            Write(archive, "ppt/slideMasters/slideMaster1.xml", SlideMaster());
            Write(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", SlideMasterRelationships());
            Write(archive, "ppt/slideLayouts/slideLayout1.xml", SlideLayout());
            Write(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", SlideLayoutRelationships());
            for (var index = 0; index < draft.Slides.Count; index++)
            {
                Write(archive, $"ppt/slides/slide{index + 1}.xml", Slide(draft.Slides[index], input, options, index + 1));
                Write(archive, $"ppt/slides/_rels/slide{index + 1}.xml.rels", SlideRelationships());
            }
        }
        return new RenderedPresentation(AssetRepresentationTypes.Pptx, SafeFileName(draft.Title, ".pptx"), "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml".Replace(".main+xml", ""), stream.ToArray());
    }

    private string Slide(PresentationSlide slide, PresentationGenerationInput input, PresentationGenerationOptions options, int number)
    {
        shapeId = 1;
        var rtl = input.Language is "ar" or "ku" || slide.Title.Any(character => character >= '\u0600' && character <= '\u08ff');
        var language = rtl ? (input.Language == "ku" ? "ku-Arab" : "ar-SA") : "en-US";
        var body = new StringBuilder();
        body.Append(ShapeTreeStart());
        body.Append(Rectangle(1, 0, 0, SlideWidth, SlideHeight, "F8FAFC", "F8FAFC", 0));
        body.Append(Rectangle(NextId(), 0, 0, SlideWidth, 170_000, "12304A", "12304A", 0));
        var titleColor = slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? "FFFFFF" : "12304A";
        var titleY = slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? 1_650_000 : 520_000;
        var titleHeight = slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? 1_400_000 : 700_000;
        body.Append(TextBox(NextId(), 720_000, titleY, 10_750_000, titleHeight, slide.Title, titleColor, slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? 30 : 24, rtl, language, true, slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? "12304A" : "EAF3F8"));
        if (!string.IsNullOrWhiteSpace(slide.Subtitle)) body.Append(TextBox(NextId(), 760_000, titleY + titleHeight - 50_000, 10_500_000, 520_000, slide.Subtitle!, slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? "EAF3F8" : "425466", 14, rtl, language, false, null));

        var contentY = slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? 3_450_000 : 1_520_000;
        var contentHeight = slide.Type is PresentationSlideTypes.Title or PresentationSlideTypes.Section or PresentationSlideTypes.Closing ? 2_250_000 : 4_650_000;
        RenderBlocks(body, slide, contentY, contentHeight, rtl, language);
        body.Append(TextBox(NextId(), 10_800_000, 6_420_000, 600_000, 220_000, number.ToString(), "60758A", 9, false, "en-US", false, null));
        body.Append("</p:spTree>");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sld xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld name=\"Slide {number}\">{body}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private void RenderBlocks(StringBuilder body, PresentationSlide slide, int y, int height, bool rtl, string language)
    {
        if (slide.Blocks.Count == 0) return;
        var blockHeight = Math.Max(500_000, height / Math.Max(1, Math.Min(slide.Blocks.Count, 4)));
        var index = 0;
        foreach (var block in slide.Blocks.Take(4))
        {
            var blockY = y + index * blockHeight;
            switch (block.Type.ToLowerInvariant())
            {
                case PresentationBlockTypes.Bullets:
                case PresentationBlockTypes.Timeline:
                case PresentationBlockTypes.Process:
                    var lines = block.Items.Count > 0 ? string.Join("\n", block.Items.Select((item, itemIndex) => block.Type.Equals(PresentationBlockTypes.Bullets, StringComparison.OrdinalIgnoreCase) ? $"• {item}" : $"{itemIndex + 1}. {item}")) : block.Text ?? string.Empty;
                    body.Append(Card(NextId(), 760_000, blockY, 10_500_000, blockHeight - 130_000, "FFFFFF", "DCE6ED"));
                    body.Append(TextBox(NextId(), 960_000, blockY + 170_000, 10_100_000, blockHeight - 300_000, lines, "253B53", 15, rtl, language, false, null));
                    break;
                case PresentationBlockTypes.Columns:
                    RenderColumns(body, block, blockY, blockHeight, rtl, language);
                    break;
                case PresentationBlockTypes.Metrics:
                    RenderMetrics(body, block, blockY, blockHeight, rtl, language);
                    break;
                case PresentationBlockTypes.Table:
                    RenderTable(body, block, blockY, blockHeight, rtl, language);
                    break;
                case PresentationBlockTypes.Quote:
                    body.Append(Card(NextId(), 760_000, blockY, 10_500_000, blockHeight - 130_000, "EAF3F8", "B7DCE5"));
                    body.Append(TextBox(NextId(), 1_000_000, blockY + 180_000, 10_000_000, blockHeight - 340_000, $"“{block.Text ?? block.Value ?? string.Empty}”", "12304A", 21, rtl, language, false, null));
                    break;
                default:
                    body.Append(TextBox(NextId(), 820_000, blockY + 110_000, 10_300_000, blockHeight - 180_000, block.Text ?? string.Join("\n", block.Items), "253B53", 16, rtl, language, false, null));
                    break;
            }
            index++;
        }
    }

    private void RenderColumns(StringBuilder body, PresentationContentBlock block, int y, int height, bool rtl, string language)
    {
        var columns = block.Columns.Take(3).ToArray();
        if (columns.Length == 0) return;
        var width = 10_200_000 / columns.Length;
        for (var index = 0; index < columns.Length; index++)
        {
            var x = 820_000 + index * width;
            body.Append(Card(NextId(), x, y, width - 180_000, height - 130_000, "FFFFFF", "DCE6ED"));
            body.Append(TextBox(NextId(), x + 140_000, y + 160_000, width - 460_000, 420_000, columns[index].Heading, "087E8B", 15, rtl, language, true, null));
            body.Append(TextBox(NextId(), x + 140_000, y + 680_000, width - 460_000, height - 850_000, string.Join("\n", columns[index].Items.Select(item => $"• {item}")), "253B53", 13, rtl, language, false, null));
        }
    }

    private void RenderMetrics(StringBuilder body, PresentationContentBlock block, int y, int height, bool rtl, string language)
    {
        var metrics = block.Metrics.Take(4).ToArray();
        if (metrics.Length == 0) return;
        var width = 10_200_000 / metrics.Length;
        for (var index = 0; index < metrics.Length; index++)
        {
            var x = 820_000 + index * width;
            body.Append(Card(NextId(), x, y, width - 180_000, Math.Min(height - 130_000, 2_200_000), "FFFFFF", "DCE6ED"));
            body.Append(TextBox(NextId(), x + 140_000, y + 180_000, width - 460_000, 340_000, metrics[index].Label, "60758A", 11, rtl, language, false, null));
            body.Append(TextBox(NextId(), x + 140_000, y + 610_000, width - 460_000, 650_000, metrics[index].Value, "12304A", 24, rtl, language, true, null));
            if (!string.IsNullOrWhiteSpace(metrics[index].Detail)) body.Append(TextBox(NextId(), x + 140_000, y + 1_420_000, width - 460_000, 480_000, metrics[index].Detail!, "425466", 10, rtl, language, false, null));
        }
    }

    private void RenderTable(StringBuilder body, PresentationContentBlock block, int y, int height, bool rtl, string language)
    {
        var rows = block.Rows.Take(8).ToArray();
        if (rows.Length == 0) return;
        var columns = Math.Clamp(rows.Max(row => row.Cells.Count), 1, 8);
        var x = 820_000;
        var width = 10_300_000;
        var rowHeight = Math.Max(260_000, Math.Min(620_000, (height - 180_000) / rows.Length));
        body.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{NextId()}\" name=\"Editable table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{rowHeight * rows.Length}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}</a:tableStyleId></a:tblPr><a:tblGrid>{string.Concat(Enumerable.Repeat($"<a:gridCol w=\"{width / columns}\"/>", columns))}</a:tblGrid>");
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            body.Append($"<a:tr h=\"{rowHeight}\">");
            for (var col = 0; col < columns; col++)
            {
                var text = col < rows[rowIndex].Cells.Count ? rows[rowIndex].Cells[col] : string.Empty;
                body.Append($"<a:tc><a:txBody><a:bodyPr rtlCol=\"{(rtl ? "1" : "0")}\"/><a:lstStyle/><a:p><a:pPr algn=\"{(rtl ? "r" : "l")}\" rtl=\"{(rtl ? "1" : "0")}\"/><a:r><a:rPr lang=\"{language}\" sz=\"{(rowIndex == 0 ? 1200 : 1050)}\" b=\"{(rowIndex == 0 ? "1" : "0")}\" typeface=\"{Font(language)}\"/><a:t>{Escape(text)}</a:t></a:r><a:endParaRPr lang=\"{language}\"/></a:p></a:txBody><a:tcPr/></a:tc>");
            }
            body.Append("</a:tr>");
        }
        body.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
    }

    private string ShapeTreeStart() => "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";
    private string Card(int id, int x, int y, int cx, int cy, string fill, string line) => Rectangle(id, x, y, cx, cy, fill, line, 12_000);
    private string Rectangle(int id, int x, int y, int cx, int cy, string fill, string line, int lineWidth) => $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill><a:ln w=\"{lineWidth}\"><a:solidFill><a:srgbClr val=\"{line}\"/></a:solidFill></a:ln></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>";

    private string TextBox(int id, int x, int y, int cx, int cy, string text, string color, int size, bool rtl, string language, bool bold, string? fill)
    {
        var paragraphs = string.Join("", text.Replace("\r", "").Split('\n').Select(line => $"<a:p><a:pPr algn=\"{(rtl ? "r" : "l")}\" rtl=\"{(rtl ? "1" : "0")}\"/><a:r><a:rPr lang=\"{language}\" sz=\"{size * 100}\" b=\"{(bold ? "1" : "0")}\" typeface=\"{Font(language)}\"/><a:t>{Escape(line)}</a:t></a:r><a:endParaRPr lang=\"{language}\"/></a:p>"));
        var fillXml = fill is null ? "<a:noFill/>" : $"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{fillXml}<a:ln><a:noFill/></a:ln></p:spPr><p:txBody><a:bodyPr wrap=\"square\" rtlCol=\"{(rtl ? "1" : "0")}\"/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
    }

    private int NextId() => ++shapeId;
    private static string Font(string language) => language is "ar-SA" or "ku-Arab" ? "Noto Sans Arabic" : "Aptos";
    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
    private static string SafeFileName(string title, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (safe.Length == 0) safe = "Taslim presentation";
        return (safe.Length > 120 ? safe[..120] : safe) + extension;
    }

    private static string ContentTypes(int slideCount) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/><Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/><Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>{string.Concat(Enumerable.Range(1, slideCount).Select(index => $"<Override PartName=\"/ppt/slides/slide{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>"))}</Types>";
    private static string RootRelationships() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rel}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>";
    private static string Presentation(int slideCount) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:presentation xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>{string.Concat(Enumerable.Range(1, slideCount).Select(index => $"<p:sldId id=\"{255 + index}\" r:id=\"rId{index + 1}\"/>"))}</p:sldIdLst><p:sldSz cx=\"{SlideWidth}\" cy=\"{SlideHeight}\" type=\"screen16x9\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/><p:defaultTextStyle/></p:presentation>";
    private static string PresentationRelationships(int slideCount) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rel}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>{string.Concat(Enumerable.Range(1, slideCount).Select(index => $"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{index}.xml\"/>"))}</Relationships>";
    private static string SlideMasterRelationships() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rel}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>";
    private static string SlideLayoutRelationships() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rel}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>";
    private static string SlideRelationships() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rel}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>";
    private static string SlideMaster() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:sldMaster xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld name=\"Taslim Master\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"1\" r:id=\"rId2\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>";
    private static string SlideLayout() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:sldLayout xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\" type=\"blank\"><p:cSld name=\"Blank\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";
    private static string Theme() => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><a:theme xmlns:a=\"{A}\" name=\"Taslim Professional\"><a:themeElements><a:clrScheme name=\"Taslim Navy\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"12304A\"/></a:dk2><a:lt2><a:srgbClr val=\"F8FAFC\"/></a:lt2><a:accent1><a:srgbClr val=\"087E8B\"/></a:accent1><a:accent2><a:srgbClr val=\"1D6FA5\"/></a:accent2><a:accent3><a:srgbClr val=\"E7A93B\"/></a:accent3><a:accent4><a:srgbClr val=\"60758A\"/></a:accent4><a:accent5><a:srgbClr val=\"B7DCE5\"/></a:accent5><a:accent6><a:srgbClr val=\"253B53\"/></a:accent6><a:hlink><a:srgbClr val=\"1D6FA5\"/></a:hlink><a:folHlink><a:srgbClr val=\"60758A\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"Taslim Fonts\"><a:majorFont><a:latin typeface=\"Aptos Display\"/><a:ea typeface=\"\"/><a:cs typeface=\"Noto Sans Arabic\"/></a:majorFont><a:minorFont><a:latin typeface=\"Aptos\"/><a:ea typeface=\"\"/><a:cs typeface=\"Noto Sans Arabic\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"Taslim Format\"><a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/></a:fmtScheme></a:themeElements></a:theme>";
}
