using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record FlorenceStoredResult(long ImageId, string Path, string Task, string Text, string State, string Provider, DateTime CreatedUtc);

public sealed class FlorenceResultRepository(PicerDatabase database)
{
    public IReadOnlyList<ImageRecord> GetPending(string task, string modelVersion)
    {
        var images = database.GetImages(includeMissing: false);
        return images.Where(image => image.Status == ImageStatus.Active && File.Exists(image.Path) && !IsCurrent(image, task, modelVersion)).ToArray();
    }
    public bool IsCurrent(ImageRecord image, string task, string modelVersion)
    {
        using var command = database.Command("SELECT COUNT(*) FROM florence_image_results WHERE image_id=$id AND task=$task AND model_version=$model AND source_file_size=$size AND source_modified_utc=$modified");
        command.Parameters.AddWithValue("$id", image.Id); command.Parameters.AddWithValue("$task", task); command.Parameters.AddWithValue("$model", modelVersion);
        command.Parameters.AddWithValue("$size", image.FileSize); command.Parameters.AddWithValue("$modified", image.ModifiedUtc.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }
    public void SaveSuggested(ImageRecord image, FlorenceTask task, FlorenceExecutionProvider provider, string text)
    {
        using var command = database.Command("""
            INSERT INTO florence_image_results(image_id,task,model_version,provider,source_file_size,source_modified_utc,result_text,state,created_utc)
            VALUES($id,$task,$model,$provider,$size,$modified,$text,'suggested',$now)
            ON CONFLICT(image_id,task) DO UPDATE SET model_version=excluded.model_version,provider=excluded.provider,
            source_file_size=excluded.source_file_size,source_modified_utc=excluded.source_modified_utc,result_text=excluded.result_text,state='suggested',created_utc=excluded.created_utc,reviewed_utc=NULL
            """);
        command.Parameters.AddWithValue("$id", image.Id); command.Parameters.AddWithValue("$task", task.ToString()); command.Parameters.AddWithValue("$model", "Florence-2-base-ft-int8-e88a44e");
        command.Parameters.AddWithValue("$provider", provider.ToString()); command.Parameters.AddWithValue("$size", image.FileSize); command.Parameters.AddWithValue("$modified", image.ModifiedUtc.ToString("O")); command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }
    public IReadOnlyList<FlorenceStoredResult> GetForReview(int limit = 500)
    {
        using var command = database.Command("SELECT f.image_id,i.path,f.task,f.result_text,f.state,f.provider,f.created_utc FROM florence_image_results f JOIN images i ON i.id=f.image_id ORDER BY CASE f.state WHEN 'suggested' THEN 0 ELSE 1 END,f.created_utc DESC LIMIT $limit"); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var results = new List<FlorenceStoredResult>();
        while (reader.Read()) results.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), DateTime.Parse(reader.GetString(6)))); return results;
    }
    public void Review(long imageId, string task, bool accepted)
    {
        using var command = database.Command("UPDATE florence_image_results SET state=$state,reviewed_utc=$now WHERE image_id=$id AND task=$task");
        command.Parameters.AddWithValue("$state", accepted ? "accepted" : "rejected"); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", imageId); command.Parameters.AddWithValue("$task", task); command.ExecuteNonQuery();
    }
}
