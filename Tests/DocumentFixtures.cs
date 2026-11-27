using System.IO;
using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Small document files made in code, so no binary is kept in the repository.</summary>
public static class DocumentFixtures
{
    /// <summary>A PDF made by PdfPig's own writer: a page for each entry, with its lines; an empty entry is a page with no text.</summary>
    public static byte[] Pdf(params string[] pages)
    {
        PdfDocumentBuilder builder = new();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (string text in pages)
        {
            PdfPageBuilder page = builder.AddPage(PageSize.A4);
            double y = 780;
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                page.AddText(line, 11, new PdfPoint(40, y), font);
                y -= 16;
            }
        }
        return builder.Build();
    }

    /// <summary>A PDF written out by hand, object by object, so reading it is not a round trip through the library that reads it: one page for each content stream, in the order given,
    /// all using Helvetica. <paramref name="flate"/> packs the streams; <paramref name="trailer"/> adds entries to the trailer.</summary>
    public static byte[] RawPdf(IReadOnlyList<byte[]> contents, bool flate = false, string trailer = "", string extraObject = null)
    {
        MemoryStream file = new();
        List<long> offsets = [];
        void Text(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        void Object(string body, byte[] stream = null)
        {
            offsets.Add(file.Position);
            Text($"{offsets.Count} 0 obj\n{body}\n");
            if (stream is not null)
            {
                Text("stream\n");
                file.Write(stream);
                Text("\nendstream\n");
            }
            Text("endobj\n");
        }
        Text("%PDF-1.4\n");
        // 1 catalog, 2 pages, 3 font, then a page and its content for each.
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, contents.Count).Select(i => $"{4 + i * 2} 0 R"))}] /Count {contents.Count} >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < contents.Count; i++)
        {
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            byte[] data = contents[i];
            if (flate)
            {
                MemoryStream packed = new();
                using (ZLibStream zlib = new(packed, CompressionLevel.SmallestSize, true))
                {
                    zlib.Write(data);
                }
                data = packed.ToArray();
            }
            Object($"<< /Length {data.Length}{(flate ? " /Filter /FlateDecode" : "")} >>", data);
        }
        if (extraObject is not null)
        {
            Object(extraObject);
        }
        long xref = file.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            Text($"{offset:D10} 00000 n \n");
        }
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R {trailer.Replace("{extra}", $"{offsets.Count} 0 R")} >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>A tiny PDF that overflows the parser's stack while it is being opened, before any check of ours can run: the page tree's only entry is an object that refers to itself.
    /// Reading it ends the process that does, so it must only ever be given to a worker.</summary>
    public static byte[] StackOverflowAtOpen()
    {
        MemoryStream file = new();
        List<long> offsets = [];
        void Text(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        Text("%PDF-1.4\n");
        foreach (string body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "3 0 R" })
        {
            offsets.Add(file.Position);
            Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        long xref = file.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            Text($"{offset:D10} 00000 n \n");
        }
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>The bytes packed as a flate stream holds them.</summary>
    public static byte[] Deflate(byte[] data)
    {
        MemoryStream packed = new();
        using (ZLibStream zlib = new(packed, CompressionLevel.SmallestSize, true))
        {
            zlib.Write(data);
        }
        return packed.ToArray();
    }

    /// <summary>A one-page PDF from loose parts, for files built to misbehave: <paramref name="page"/> adds entries to the page (object 3), and the objects follow as 4, 5 and so on, each a
    /// stream with these dictionary entries, or (with no stream) the object written as it is.</summary>
    public static byte[] Objects(string page, params (string Dictionary, byte[] Stream)[] objects) => Document(page, "", "", objects);

    /// <summary><see cref="Objects"/> with entries added to the catalog (object 1) and to the page tree's node (object 2). A <c>/Parent</c> in <paramref name="page"/> replaces the true one.</summary>
    public static byte[] Document(string page, string catalog, string pages, params (string Dictionary, byte[] Stream)[] objects)
    {
        MemoryStream file = new();
        List<long> offsets = [];
        void Text(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        void Object(string dictionary, byte[] stream)
        {
            offsets.Add(file.Position);
            Text($"{offsets.Count} 0 obj\n");
            if (stream is null)
            {
                Text(dictionary + "\n");
            }
            else
            {
                Text($"<< {dictionary} /Length {stream.Length} >>\nstream\n");
                file.Write(stream);
                Text("\nendstream\n");
            }
            Text("endobj\n");
        }
        Text("%PDF-1.4\n");
        Object($"<< /Type /Catalog /Pages 2 0 R {catalog} >>", null);
        Object($"<< /Type /Pages /Kids [3 0 R] /Count 1 {pages} >>", null);
        Object($"<< /Type /Page {(page.Contains("/Parent") ? "" : "/Parent 2 0 R")} /MediaBox [0 0 595 842] {page} >>", null);
        foreach ((string dictionary, byte[] stream) in objects)
        {
            Object(dictionary, stream);
        }
        long xref = file.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            Text($"{offset:D10} 00000 n \n");
        }
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>A content stream that writes lines of text.</summary>
    public static byte[] Lines(params string[] lines)
        => Encoding.ASCII.GetBytes("BT /F1 12 Tf 40 780 Td " + string.Join(" 0 -18 Td ", lines.Select(l => $"({l}) Tj")) + " ET");

    /// <summary>A .docx holding this body XML (the inside of <c>w:body</c>).</summary>
    public static byte[] Docx(string body, string documentXml = null)
    {
        string xml = documentXml ?? "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\">"
            + $"<w:body>{body}</w:body></w:document>";
        return Zip(("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>")), ("word/document.xml", Encoding.UTF8.GetBytes(xml)));
    }

    /// <summary>A paragraph of a .docx, in a style when one is named.</summary>
    public static string Paragraph(string text, string style = null)
        => $"<w:p>{(style is null ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>")}<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    /// <summary>A zip of these entries.</summary>
    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        MemoryStream file = new();
        using (ZipArchive zip = new(file, ZipArchiveMode.Create, true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                using Stream entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(data);
            }
        }
        return file.ToArray();
    }
}
