using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using System.Globalization;
using Picer.Core.Models;
using SixLabors.ImageSharp;
using Meta = MetadataExtractor.ImageMetadataReader;

namespace Picer.Core.Metadata;

public static class ImageMetadataReader
{
    public static ImageMetadata Read(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadStream(fs);
    }

    public static ImageMetadata Read(Stream stream) => ReadStream(stream);

    private static ImageMetadata ReadStream(Stream stream)
    {
        int width = 0, height = 0;
        var origPos = stream.CanSeek ? stream.Position : 0;

        try
        {
            var info = Image.Identify(stream);
            if (info != null)
            {
                width = info.Width;
                height = info.Height;
            }
        }
        catch (Exception)
        {
        }

        if (stream.CanSeek)
            stream.Position = origPos;

        DateTime? taken = null;
        string? camera = null;
        string? gps = null;

        try
        {
            foreach (var dir in Meta.ReadMetadata(stream))
            {
                if (dir is ExifDirectoryBase exif)
                {
                    if (taken is null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dtOrig))
                        taken = DateTime.SpecifyKind(dtOrig, DateTimeKind.Utc);
                    if (taken is null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt))
                        taken = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

                    if (camera is null)
                    {
                        var make = exif.GetDescription(ExifDirectoryBase.TagMake);
                        var model = exif.GetDescription(ExifDirectoryBase.TagModel);
                        var parts = new[] { make, model }.Where(s => !string.IsNullOrWhiteSpace(s));
                        camera = string.Join(" ", parts);
                        if (camera.Length == 0)
                            camera = null;
                    }
                }

                if (gps is null && dir is GpsDirectory gpsDir)
                {
                    var location = gpsDir.GetGeoLocation();
                    if (location is { } coordinates && !coordinates.IsZero)
                        gps = string.Create(CultureInfo.InvariantCulture, $"{coordinates.Latitude:F7},{coordinates.Longitude:F7}");
                }
            }
        }
        catch (Exception)
        {
        }

        return new ImageMetadata
        {
            Width = width,
            Height = height,
            TakenUtc = taken,
            Camera = camera,
            Gps = gps
        };
    }
}
