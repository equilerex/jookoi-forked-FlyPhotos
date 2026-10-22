#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Services;

/// <summary>
/// Reads and writes <c>xmp:Rating</c> (0-5). JPEG embeds the XMP packet in an APP1 segment without touching the
/// image data. Everything else gets a sidecar .xmp: proprietary RAW containers are not safe to rewrite.
/// </summary>
internal static class XmpRatingWriter
{
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace MicrosoftPhoto = "http://ns.microsoft.com/photo/1.0/";

    private const string XmpSegmentId = "http://ns.adobe.com/xap/1.0/";
    private const int MaxXmpPayloadBytes = 65504; // 65535 - 2 length bytes - 29 header bytes

    // Marker bytes
    private const int Soi = 0xD8, Eoi = 0xD9, Sos = 0xDA, App0 = 0xE0, App1 = 0xE1, App2 = 0xE2;

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".jfif" };

    /// <summary>Rating 0-5 from the embedded packet (JPEG) or sidecar; null when the file has no rating info.</summary>
    public static int? ReadRating(string filePath, bool isRaw, SidecarStyle style)
    {
        try
        {
            if (IsEmbeddable(filePath))
            {
                // A sidecar here comes from Fly's fallback (multi-picture JPEG) or from darktable/digiKam.
                // Fly writes both, other apps may write only one, so the newer one wins.
                var embeddedRating = IsJpeg(filePath) ? ReadJpegRating(filePath) : ReadTiffRating(filePath);
                var sidecar = NewestExisting(SidecarPaths(filePath, false, SidecarStyle.OpenSource));
                if (sidecar == null) return embeddedRating;
                var sidecarRating = ParseRating(File.ReadAllText(sidecar, Encoding.UTF8));
                return File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(filePath)
                    ? sidecarRating ?? embeddedRating
                    : embeddedRating ?? sidecarRating;
            }
            return ReadSidecarRating(SidecarPaths(filePath, isRaw, style));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes rating 0-5 (0 clears). Never throws.</summary>
    public static RatingResult WriteRating(string filePath, int rating, bool isRaw, SidecarStyle style)
    {
        rating = Math.Clamp(rating, 0, 5);
        var gate = PathLocks.GetOrAdd(filePath, _ => new object());
        lock (gate)
        {
            try
            {
                var embedded = IsJpeg(filePath) ? TryWriteJpeg(filePath, rating)
                    : IsTiffBased(filePath) && TryWriteTiff(filePath, rating);
                if (embedded)
                {
                    // Keep an existing sidecar in step, or its old value could win the next read. Never create one.
                    var existingSidecars = SidecarPaths(filePath, false, SidecarStyle.OpenSource).Where(File.Exists).ToList();
                    if (existingSidecars.Count > 0) WriteSidecars(existingSidecars, rating);
                    return new RatingResult(true, rating: rating);
                }
                if (IsEmbeddable(filePath))
                {
                    // Cannot be patched safely (multi-picture JPEG, BigTIFF): keep the file intact and use a sidecar.
                    WriteSidecars(SidecarPaths(filePath, false, SidecarStyle.OpenSource), rating);
                    return new RatingResult(true, rating: rating);
                }
                WriteSidecars(SidecarPaths(filePath, isRaw, style), rating, createOnlyFirst: style != SidecarStyle.Both);
                return new RatingResult(true, rating: rating);
            }
            catch (Exception ex)
            {
                return new RatingResult(false, ex.Message);
            }
        }
    }

    /// <summary>Sidecars that belong to this file, for rename/delete housekeeping (existing files only).</summary>
    public static List<string> ExistingSidecars(string filePath, bool isRaw) =>
        SidecarPaths(filePath, isRaw && !IsEmbeddable(filePath), SidecarStyle.Adobe).Where(File.Exists).ToList();

    /// <summary>Where <paramref name="oldSidecar"/> goes when <paramref name="oldFilePath"/> becomes <paramref name="newFilePath"/>.</summary>
    public static string RenamedSidecar(string oldSidecar, string oldFilePath, string newFilePath) =>
        string.Equals(oldSidecar, oldFilePath + ".xmp", StringComparison.OrdinalIgnoreCase)
            ? newFilePath + ".xmp"
            : Path.ChangeExtension(newFilePath, ".xmp");

    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".tif", ".tiff" };

    private static bool IsJpeg(string filePath) => JpegExtensions.Contains(Path.GetExtension(filePath));

    // DNG is TIFF-based and Lightroom ignores sidecars for it, so it embeds like TIFF.
    private static bool IsTiffBased(string filePath) => TiffExtensions.Contains(Path.GetExtension(filePath));

    private static bool IsEmbeddable(string filePath) => IsJpeg(filePath) || IsTiffBased(filePath);

    // ---------- XMP packet ----------

    /// <summary>Returns <paramref name="xml"/> with the rating set, or a fresh packet when it is null. Throws XmlException on bad XML.</summary>
    internal static string ApplyRating(string? xml, int rating)
    {
        if (string.IsNullOrWhiteSpace(xml)) return NewPacket(rating);

        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf == null)
        {
            if (doc.Root == null) return NewPacket(rating);
            rdf = new XElement(Rdf + "RDF");
            doc.Root.Add(rdf);
        }

        var descriptions = rdf.Elements(Rdf + "Description").ToList();
        if (descriptions.Count == 0)
        {
            var created = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(created);
            descriptions.Add(created);
        }

        var value = rating.ToString(CultureInfo.InvariantCulture);
        var set = false;
        foreach (var description in descriptions)
        {
            set |= SetIfPresent(description, Xmp + "Rating", value);
            SetIfPresent(description, MicrosoftPhoto + "Rating", MicrosoftPhotoRating(rating));
        }

        if (!set)
        {
            var first = descriptions[0];
            if (first.GetPrefixOfNamespace(Xmp) == null)
                first.SetAttributeValue(XNamespace.Xmlns + "xmp", Xmp.NamespaceName);
            first.SetAttributeValue(Xmp + "Rating", value);
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    internal static int? ParseRating(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var doc = XDocument.Parse(xml);
        foreach (var description in doc.Descendants(Rdf + "Description"))
        {
            var text = description.Attribute(Xmp + "Rating")?.Value ?? description.Element(Xmp + "Rating")?.Value;
            if (text == null) continue;
            // Spec type is Real, so "3.0" is valid; -1 means rejected and shows no stars.
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) continue;
            return Math.Clamp((int)Math.Round(parsed), 0, 5);
        }
        return null;
    }

    private static bool SetIfPresent(XElement description, XName name, string value)
    {
        var attribute = description.Attribute(name);
        if (attribute != null)
        {
            attribute.Value = value;
            return true;
        }
        var element = description.Element(name);
        if (element == null) return false;
        element.Value = value;
        return true;
    }

    private static string MicrosoftPhotoRating(int rating) => rating switch
    {
        0 => "0", 1 => "1", 2 => "25", 3 => "50", 4 => "75", _ => "99"
    };

    private static string NewPacket(int rating) =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"" +
        rating.ToString(CultureInfo.InvariantCulture) + "\"/>" +
        "</rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";

    // ---------- sidecar ----------

    private static List<string> SidecarPaths(string filePath, bool isRaw, SidecarStyle style)
    {
        var openSource = filePath + ".xmp";
        // Only proprietary RAW uses the Adobe name: IMG_1234.xmp would also match IMG_1234.HEIC / .JPG.
        if (!isRaw) return [openSource];

        var adobe = Path.ChangeExtension(filePath, ".xmp");
        return style switch
        {
            SidecarStyle.OpenSource => [openSource, adobe],
            SidecarStyle.Both => [adobe, openSource],
            _ => [adobe, openSource]
        };
    }

    private static string? NewestExisting(List<string> candidates) =>
        candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    private static int? ReadSidecarRating(List<string> candidates)
    {
        var newest = NewestExisting(candidates);
        return newest == null ? null : ParseRating(File.ReadAllText(newest, Encoding.UTF8));
    }

    /// <summary>
    /// Updates every existing sidecar in <paramref name="candidates"/>. When none exists, creates the first one
    /// (or all of them when <paramref name="createOnlyFirst"/> is false). Rating 0 never creates a file.
    /// </summary>
    private static void WriteSidecars(List<string> candidates, int rating, bool createOnlyFirst = true)
    {
        var existing = candidates.Where(File.Exists).ToList();
        var targets = existing.Count > 0 && createOnlyFirst
            ? existing
            : createOnlyFirst ? [candidates[0]] : candidates;

        foreach (var target in targets)
        {
            var exists = File.Exists(target);
            if (!exists && rating == 0) continue;
            var current = exists ? File.ReadAllText(target, Encoding.UTF8) : null;
            WriteAtomic(target, new UTF8Encoding(false).GetBytes(ApplyRating(current, rating)));
        }
    }

    private static void WriteAtomic(string target, byte[] contents)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, contents);
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ---------- JPEG ----------

    /// <summary>Start includes any 0xFF fill bytes before the marker; PayloadStart is after the length field.</summary>
    private readonly record struct Segment(long Start, long PayloadStart, long End, int Marker, string Id);

    private sealed class JpegLayout
    {
        public List<Segment> Segments { get; } = [];
    }

    private static int? ReadJpegRating(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var layout = ScanJpeg(stream);
        var xmp = layout?.Segments.FirstOrDefault(s => s.Marker == App1 && s.Id == XmpSegmentId);
        return xmp is { } segment ? ParseRating(ReadXmp(stream, segment)) : null;
    }

    /// <summary>Embeds the rating; false when the file cannot be patched safely (caller falls back to a sidecar).</summary>
    private static bool TryWriteJpeg(string filePath, int rating)
    {
        string temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var layout = ScanJpeg(source);
                if (layout == null) return false;

                // MPF stores the primary image size and offsets; growing the file before it would invalidate them.
                if (layout.Segments.Any(s => s.Marker == App2 && s.Id == "MPF")) return false;

                var existing = layout.Segments.Where(s => s.Marker == App1 && s.Id == XmpSegmentId)
                    .Cast<Segment?>().FirstOrDefault();
                if (existing == null && rating == 0) return true;

                var xml = ApplyRating(existing is { } e ? ReadXmp(source, e) : null, rating);
                var payload = new UTF8Encoding(false).GetBytes(xml);
                if (payload.Length > MaxXmpPayloadBytes) return false;

                var header = Encoding.ASCII.GetBytes(XmpSegmentId + "\0");
                var length = 2 + header.Length + payload.Length;

                // Replace in place, or insert after the leading JFIF/Exif segments.
                var cutStart = existing?.Start ?? InsertionOffset(layout);
                var cutEnd = existing?.End ?? cutStart;

                using var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                CopyRange(source, destination, 0, cutStart);
                destination.Write([0xFF, App1, (byte)(length >> 8), (byte)length]);
                destination.Write(header);
                destination.Write(payload);
                CopyRange(source, destination, cutEnd, source.Length);
            }

            File.Replace(temp, filePath, null);
            return true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static long InsertionOffset(JpegLayout layout)
    {
        long offset = 2;
        foreach (var segment in layout.Segments)
        {
            var leading = segment.Marker == App0 || segment is { Marker: App1, Id: "Exif" };
            if (!leading) break;
            offset = segment.End;
        }
        return offset;
    }

    private static string ReadXmp(Stream stream, Segment segment)
    {
        var start = segment.PayloadStart + XmpSegmentId.Length + 1;
        var buffer = new byte[segment.End - start];
        stream.Position = start;
        stream.ReadExactly(buffer);
        // Some writers pad the segment with NULs, which XDocument rejects. Same trim as the TIFF path.
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0', ' ');
    }

    private static JpegLayout? ScanJpeg(Stream stream)
    {
        if (stream.Length < 4) return null;
        stream.Position = 0;
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != Soi) return null;

        var layout = new JpegLayout();
        var idBuffer = new byte[40];
        long position = 2;
        while (true)
        {
            stream.Position = position;
            if (stream.ReadByte() != 0xFF) return null;
            int marker;
            do marker = stream.ReadByte(); while (marker == 0xFF);
            if (marker < 0) return null;
            if (marker is Sos or Eoi) return layout;

            // Markers without a length field
            if (marker is 0x01 or Soi || marker is >= 0xD0 and <= 0xD7 || marker == 0x00)
            {
                position = stream.Position;
                continue;
            }

            var high = stream.ReadByte();
            var low = stream.ReadByte();
            if (low < 0) return null;
            var length = (high << 8) | low;
            if (length < 2) return null;

            var payloadStart = stream.Position;
            var end = payloadStart + length - 2;
            if (end > stream.Length) return null;

            var id = "";
            if (marker is App1 or App2)
            {
                var read = stream.Read(idBuffer, 0, (int)Math.Min(idBuffer.Length, length - 2));
                var nul = Array.IndexOf(idBuffer, (byte)0, 0, read);
                id = Encoding.ASCII.GetString(idBuffer, 0, nul < 0 ? read : nul);
            }

            layout.Segments.Add(new Segment(position, payloadStart, end, marker, id));
            position = end;
        }
    }

    // ---------- TIFF / DNG ----------

    private const int TiffXmpTag = 700;
    private const int TiffByte = 1;

    private sealed class TiffLayout
    {
        public bool LittleEndian;
        public long Ifd0Offset;
        public int EntryCount;
        public uint NextIfdOffset;
        public byte[] Entries = []; // EntryCount * 12 raw bytes, sorted by tag

        public int Tag(int i) => U16(Entries, i * 12);
        public long Count(int i) => U32(Entries, i * 12 + 4);
        public long Value(int i) => U32(Entries, i * 12 + 8);

        public int U16(byte[] b, int o) => LittleEndian ? b[o] | b[o + 1] << 8 : b[o] << 8 | b[o + 1];

        public long U32(byte[] b, int o) => LittleEndian
            ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24)
            : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

        public void Put16(byte[] b, int o, int v)
        {
            if (LittleEndian) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
            else { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        }

        public void Put32(byte[] b, int o, long v)
        {
            if (LittleEndian) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
            else { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        }

        /// <summary>Index of the XMP entry, or -1.</summary>
        public int FindXmp()
        {
            for (var i = 0; i < EntryCount; i++)
                if (Tag(i) == TiffXmpTag) return i;
            return -1;
        }
    }

    /// <summary>Classic TIFF only (magic 42). BigTIFF and malformed files return null.</summary>
    private static TiffLayout? ReadTiffLayout(Stream stream)
    {
        if (stream.Length < 16 || stream.Length >= uint.MaxValue - 1_000_000) return null;
        var header = new byte[8];
        stream.Position = 0;
        stream.ReadExactly(header);
        var layout = new TiffLayout();
        if (header[0] == 'I' && header[1] == 'I') layout.LittleEndian = true;
        else if (!(header[0] == 'M' && header[1] == 'M')) return null;
        if (layout.U16(header, 2) != 42) return null;

        layout.Ifd0Offset = layout.U32(header, 4);
        if (layout.Ifd0Offset < 8 || layout.Ifd0Offset + 2 > stream.Length) return null;

        var countBytes = new byte[2];
        stream.Position = layout.Ifd0Offset;
        stream.ReadExactly(countBytes);
        layout.EntryCount = layout.U16(countBytes, 0);
        var entriesLength = layout.EntryCount * 12;
        if (layout.Ifd0Offset + 2 + entriesLength + 4 > stream.Length) return null;

        layout.Entries = new byte[entriesLength];
        stream.ReadExactly(layout.Entries);
        var next = new byte[4];
        stream.ReadExactly(next);
        layout.NextIfdOffset = (uint)layout.U32(next, 0);
        return layout;
    }

    private static string? ReadTiffXmp(Stream stream, TiffLayout layout)
    {
        var index = layout.FindXmp();
        if (index < 0) return null;
        var count = layout.Count(index);
        var offset = layout.Value(index);
        if (count <= 4 || offset + count > stream.Length) return null;

        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0', ' ');
    }

    private static int? ReadTiffRating(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var layout = ReadTiffLayout(stream);
        return layout == null ? null : ParseRating(ReadTiffXmp(stream, layout));
    }

    /// <summary>
    /// Embeds the rating in IFD0's XMP tag (700) of a copy, then swaps the copy in. Existing bytes are never moved:
    /// the packet is overwritten in place when it fits, otherwise appended at the end and the tag repointed.
    /// A file without the tag gets a rewritten IFD0 appended at the end. False for files this cannot patch (BigTIFF).
    /// </summary>
    private static bool TryWriteTiff(string filePath, int rating)
    {
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(filePath, temp);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var layout = ReadTiffLayout(stream);
                if (layout == null) return false;

                var index = layout.FindXmp();
                var existingXml = ReadTiffXmp(stream, layout);
                if (existingXml == null && rating == 0) return true;

                var packet = new UTF8Encoding(false).GetBytes(ApplyRating(existingXml, rating));

                if (index >= 0 && layout.Count(index) > 4 && packet.Length <= layout.Count(index)
                    && layout.Value(index) + layout.Count(index) <= stream.Length)
                {
                    // Fits where it is: pad with spaces so the stored count stays valid.
                    var padded = new byte[layout.Count(index)];
                    Array.Fill(padded, (byte)' ');
                    packet.CopyTo(padded, 0);
                    stream.Position = layout.Value(index);
                    stream.Write(padded);
                }
                else
                {
                    var packetOffset = AppendAligned(stream, packet);
                    if (index >= 0)
                    {
                        var entry = layout.Entries.AsSpan(index * 12, 12).ToArray();
                        layout.Put16(entry, 2, TiffByte);
                        layout.Put32(entry, 4, packet.Length);
                        layout.Put32(entry, 8, packetOffset);
                        stream.Position = layout.Ifd0Offset + 2 + index * 12;
                        stream.Write(entry);
                    }
                    else
                    {
                        AppendIfdWithXmpEntry(stream, layout, packet.Length, packetOffset);
                    }
                }
            }

            File.Replace(temp, filePath, null);
            return true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Appends at an even offset (TIFF requires word alignment) and returns where the data starts.</summary>
    private static long AppendAligned(Stream stream, byte[] data)
    {
        stream.Position = stream.Length;
        if (stream.Length % 2 != 0) stream.WriteByte(0);
        var offset = stream.Position;
        stream.Write(data);
        if (stream.Length % 2 != 0) stream.WriteByte(0);
        return offset;
    }

    private static void AppendIfdWithXmpEntry(Stream stream, TiffLayout layout, int packetLength, long packetOffset)
    {
        var xmpEntry = new byte[12];
        layout.Put16(xmpEntry, 0, TiffXmpTag);
        layout.Put16(xmpEntry, 2, TiffByte);
        layout.Put32(xmpEntry, 4, packetLength);
        layout.Put32(xmpEntry, 8, packetOffset);

        // Entries must stay sorted by tag.
        var insertAt = 0;
        while (insertAt < layout.EntryCount && layout.Tag(insertAt) < TiffXmpTag) insertAt++;

        var ifd = new byte[2 + (layout.EntryCount + 1) * 12 + 4];
        layout.Put16(ifd, 0, layout.EntryCount + 1);
        Buffer.BlockCopy(layout.Entries, 0, ifd, 2, insertAt * 12);
        xmpEntry.CopyTo(ifd, 2 + insertAt * 12);
        Buffer.BlockCopy(layout.Entries, insertAt * 12, ifd, 2 + (insertAt + 1) * 12, (layout.EntryCount - insertAt) * 12);
        layout.Put32(ifd, ifd.Length - 4, layout.NextIfdOffset);

        var ifdOffset = AppendAligned(stream, ifd);

        var pointer = new byte[4];
        layout.Put32(pointer, 0, ifdOffset);
        stream.Position = 4;
        stream.Write(pointer);
    }

    private static void CopyRange(Stream source, Stream destination, long from, long to)
    {
        source.Position = from;
        var buffer = new byte[81920];
        var remaining = to - from;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
