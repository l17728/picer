namespace Picer.Core.Indexing;

public static class SupportedFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    public static bool IsSupported(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName));

    public static string OpenFileDialogFilter =>
        "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有文件|*.*";
}
