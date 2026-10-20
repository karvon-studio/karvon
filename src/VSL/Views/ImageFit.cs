using SkiaSharp;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>
/// Images for Claude: a screenshot of a large or high-DPI screen can pass the API's 5 MB per image as PNG, so it goes as
/// a JPEG then, made smaller until it fits (as the VS extension does).
/// </summary>
public static class ImageFit
{
    /// <summary>The image as it can be sent, and its file extension; the image itself when it fits or cannot be read.</summary>
    public static (byte[] Bytes, string Extension) ForClaude(byte[] image, string extension = ".png")
    {
        if (image.Length <= ClaudeAttachment.MaxBytes)
            return (image, extension);
        using var source = SKBitmap.Decode(image);
        if (source is null)
            return (image, extension);
        for (var scale = 1.0; scale > 0.15; scale *= 0.75)
        {
            var width = Math.Max(1, (int)(source.Width * scale));
            var height = Math.Max(1, (int)(source.Height * scale));
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            // JPEG has no alpha: transparent parts go onto white, not black.
            surface.Canvas.Clear(SKColors.White);
            using (var paint = new SKPaint { IsAntialias = true })
            using (var scaled = SKImage.FromBitmap(source))
                surface.Canvas.DrawImage(scaled, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 88);
            if (jpeg.Size <= ClaudeAttachment.MaxBytes)
                return (jpeg.ToArray(), ".jpg");
        }
        return (image, extension);
    }

    /// <summary>Image files over 5 MB, attached as a smaller JPEG; the other paths are left to the view model.</summary>
    public static List<string> AttachLargeImages(ClaudeViewModel vm, IEnumerable<string> paths)
    {
        var rest = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > ClaudeAttachment.MaxBytes && info.Length < 200L * 1024 * 1024
                    && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp")
                {
                    var (bytes, extension) = ForClaude(File.ReadAllBytes(path), Path.GetExtension(path));
                    if (bytes.Length <= ClaudeAttachment.MaxBytes)
                    {
                        vm.AddImage(Path.GetFileNameWithoutExtension(path) + extension, bytes);
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            rest.Add(path);
        }
        return rest;
    }
}
