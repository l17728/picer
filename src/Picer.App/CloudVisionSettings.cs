using System.IO;
using System.Text.Json;
using Picer.Core.Vision;

namespace Picer.App;

public sealed class CloudVisionSettings
{
    public bool Enabled { get; set; }
    public DomesticVisionProvider Provider { get; set; } = DomesticVisionProvider.BailianQwen;
    public int MaximumImagesPerRun { get; set; } = 100;
    public bool IncludePreciseGps { get; set; }
    public bool UseComplexModel { get; set; }
    public decimal DailyBudgetYuan { get; set; } = 2m;
    public decimal MonthlyBudgetYuan { get; set; } = 20m;

    public static CloudVisionSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<CloudVisionSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        MaximumImagesPerRun = Math.Clamp(MaximumImagesPerRun, 1, 1000);
        DailyBudgetYuan = Math.Clamp(DailyBudgetYuan, 0.1m, 10000m);
        MonthlyBudgetYuan = Math.Clamp(MonthlyBudgetYuan, DailyBudgetYuan, 100000m);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
