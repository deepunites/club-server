using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace Club.Server.Imaging;

/// <summary>Образ внутри WIM (по XML-описанию): индекс, имя, редакция, сборка, архитектура, объём после распаковки.</summary>
public sealed record WimImage(int Index, string Name, string? Description, string? EditionId, string? Build, string? Architecture, string? DefaultLanguage, long TotalBytes);

/// <summary>
/// Заголовок WIM и его XML-описание (формат WIM: заголовок 208 байт, ресурс XML — UTF-16LE без сжатия).
/// Читает только метаданные; содержимое образа не распаковывается.
/// </summary>
public static class WimFile
{
    private static readonly byte[] Magic = "MSWIM\0\0\0"u8.ToArray();
    private const int HeaderSize = 208;
    private const int XmlResourceOffset = 72;
    private const long MaxXmlBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<WimImage> ReadImages(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a WIM file");
        }

        var imageCount = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        var partNumber = BinaryPrimitives.ReadUInt16LittleEndian(header[40..]);
        var totalParts = BinaryPrimitives.ReadUInt16LittleEndian(header[42..]);
        if (partNumber != 1 || totalParts != 1)
        {
            throw new InvalidDataException("split WIM (.swm) is not supported");
        }

        // RESHDR_DISK_SHORT: 7 байт размера + 1 байт флагов, 8 байт смещения, 8 байт исходного размера.
        var xmlHeader = header.Slice(XmlResourceOffset, 24);
        var size = (long)(BinaryPrimitives.ReadUInt64LittleEndian(xmlHeader) & 0x00FF_FFFF_FFFF_FFFF);
        var flags = xmlHeader[7];
        var offset = BinaryPrimitives.ReadInt64LittleEndian(xmlHeader[8..]);
        if ((flags & 0x04) != 0)
        {
            throw new InvalidDataException("compressed XML resource");
        }

        if (size is <= 2 or > MaxXmlBytes || offset < HeaderSize || offset + size > file.Length)
        {
            throw new InvalidDataException("invalid XML resource");
        }

        var xml = new byte[size];
        file.Position = offset;
        file.ReadExactly(xml);
        var text = Encoding.Unicode.GetString(xml).TrimStart('﻿');
        var images = XDocument.Parse(text).Root!.Elements("IMAGE").Select(Parse).OrderBy(i => i.Index).ToList();
        if (images.Count != imageCount)
        {
            throw new InvalidDataException($"header says {imageCount} images, XML describes {images.Count}");
        }

        return images;
    }

    private static WimImage Parse(XElement image)
    {
        var windows = image.Element("WINDOWS");
        var version = windows?.Element("VERSION");
        var build = version is null ? null : string.Join('.', new[] { "MAJOR", "MINOR", "BUILD", "SPBUILD" }.Select(n => version.Element(n)?.Value ?? "0"));
        return new WimImage(
            int.Parse((string?)image.Attribute("INDEX") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
            image.Element("NAME")?.Value ?? "",
            NullIfEmpty(image.Element("DESCRIPTION")?.Value),
            NullIfEmpty(windows?.Element("EDITIONID")?.Value),
            build,
            Architecture(windows?.Element("ARCH")?.Value),
            NullIfEmpty(windows?.Element("LANGUAGES")?.Element("DEFAULT")?.Value),
            long.TryParse(image.Element("TOTALBYTES")?.Value, out var total) ? total : 0);
    }

    // Значения PROCESSOR_ARCHITECTURE: 0 — x86, 9 — x64, 12 — arm64.
    private static string? Architecture(string? value) => value switch
    {
        null or "" => null,
        "0" => "x86",
        "9" => "x64",
        "12" => "arm64",
        _ => value,
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
