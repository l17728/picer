using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.AiRecognition;

/// <summary>
/// Deterministic local metadata classifier used to suggest explainable tags.
/// </summary>
public static class AiSceneDetector
{
    /// <summary>
    /// Analyzes image metadata (EXIF/GPS) and returns suggested tags based on patterns.
    /// No image or metadata leaves the device.
    /// </summary>
    public static List<SuggestedTag> AnalyzeImage(ImageRecord record)
    {
        var suggestions = new List<SuggestedTag>();

        // GPS-based suggestions
        if (!string.IsNullOrEmpty(record.Gps))
        {
            suggestions.Add(new SuggestedTag("户外", Confidence.High));
            suggestions.Add(new SuggestedTag("有位置信息", Confidence.High));
            
            if (record.Gps.Contains("North") || record.Gps.Contains("南"))
                suggestions.Add(new SuggestedTag("纬度区域拍摄", Confidence.Medium));
            else if (record.Gps.Contains("East") || record.Gps.Contains("西"))
                suggestions.Add(new SuggestedTag("经度区域拍摄", Confidence.Medium));
        }

        // Camera-based suggestions  
        if (!string.IsNullOrEmpty(record.Camera))
        {
            var cam = record.Camera.ToLowerInvariant();
            if (cam.Contains("canon"))
                suggestions.Add(new SuggestedTag("佳能摄影", Confidence.Medium));
            else if (cam.Contains("nikon"))
                suggestions.Add(new SuggestedTag("尼康摄影", Confidence.Medium));
            else if (cam.Contains("sony") || cam.Contains("α"))
                suggestions.Add(new SuggestedTag("索尼摄影", Confidence.Medium));
            else if (cam.Contains("apple") || cam.Contains("iphone"))
                suggestions.Add(new SuggestedTag("手机拍摄", Confidence.Medium));
            else if (cam.Contains("dji") || cam.Contains("无人机"))
                suggestions.Add(new SuggestedTag("航拍", Confidence.Medium));
            else
                suggestions.Add(new SuggestedTag($"相机型号: {record.Camera.Split(' ')[0]}", Confidence.Low));
        }

        // Dimension-based suggestions
        if (record.Width > 0 && record.Height > 0)
        {
            float aspectRatio = (float)record.Width / record.Height;
            if (aspectRatio > 1.5f)
                suggestions.Add(new SuggestedTag("宽幅/全景", Confidence.Medium));
            else if (Math.Abs(aspectRatio - 1.0f) < 0.1f)
                suggestions.Add(new SuggestedTag("方形构图", Confidence.Medium));
                
            int megapixels = (record.Width * record.Height) / 1_000_000;
            if (megapixels >= 20)
                suggestions.Add(new SuggestedTag("高像素", Confidence.High));
            else if (megapixels >= 12)
                suggestions.Add(new SuggestedTag("标准分辨率", Confidence.Medium));
        }

        // Timestamp-based suggestions
        if (record.TakenUtc.HasValue)
        {
            var time = record.TakenUtc.Value.Hour;
            if (time >= 6 && time < 9)
                suggestions.Add(new SuggestedTag("清晨", Confidence.Medium));
            else if (time >= 17 && time < 20)
                suggestions.Add(new SuggestedTag("黄昏/傍晚", Confidence.Medium));
            else if (time >= 8 && time < 12)
                suggestions.Add(new SuggestedTag("上午", Confidence.Low));
            else if (time >= 12 && time < 17)
                suggestions.Add(new SuggestedTag("下午", Confidence.Low));
        }

        return suggestions;
    }
}
