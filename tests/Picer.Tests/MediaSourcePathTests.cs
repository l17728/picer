using Picer.Core.Data;
using Picer.Core.Indexing;

namespace Picer.Tests;

public sealed class MediaSourcePathTests
{
    [Theory]
    [InlineData("G:\\Apple照片", "G:/Apple照片")]
    [InlineData("/opt/picer-index/G:\\Apple照片", "G:/Apple照片")]
    [InlineData("D:\\opt\\picer-index\\G:\\Apple照片", "G:/Apple照片")]
    [InlineData("/opt/picer-index/photos", "/opt/picer-index/photos")]
    [InlineData("D:/opt/picer-index/photos", "D:/opt/picer-index/photos")]
    [InlineData("\\\\nas\\photos\\album", "//nas/photos/album")]
    public void NormalizeOnlyKnownWindowsTransportPrefixes(string input, string expected) =>
        Assert.Equal(expected, MediaSourcePath.Normalize(input));

    [Fact]
    public void SourceRepairPreservesIdAndRemovesOnlyEmptyAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var db = new PicerDatabase(Path.Combine(root, "test.db"));
        using (var command = db.Command("INSERT INTO sources(id,path,added_utc) VALUES(1,$first,$time),(2,$second,$time)"))
        {
            command.Parameters.AddWithValue("$first", "/opt/picer-index/G:\\Apple照片");
            command.Parameters.AddWithValue("$second", "D:\\opt\\picer-index\\G:\\Apple照片");
            command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var source = db.AddSource("G:\\Apple照片");
        Assert.Equal(1, source.Id);
        Assert.Equal("G:\\Apple照片", Assert.Single(db.GetSources()).Path);
        Assert.Equal(source.Id, db.AddSource("/opt/picer-index/G:\\Apple照片").Id);
        Assert.Equal("/mnt/photos", db.AddSource("/mnt/photos").Path);
        using (var legacy = db.Command("""
            INSERT INTO sources(id,path,added_utc) VALUES(10,$path,$now);
            INSERT INTO burst_source_state(source_id,algorithm_version,updated_utc) VALUES(10,'test',$now);
            """))
        {
            legacy.Parameters.AddWithValue("$path", "D:\\opt\\picer-index\\G:\\Apple照片");
            legacy.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            legacy.ExecuteNonQuery();
        }
        Assert.Equal(source.Id, db.AddSource("G:\\Apple照片", reconcileAliases: true).Id);
        Assert.DoesNotContain(db.GetSources(), item => item.Id == 10);
        using var state = db.Command("SELECT source_id FROM burst_source_state WHERE algorithm_version='test'");
        Assert.Equal(source.Id, Convert.ToInt64(state.ExecuteScalar()));
    }
}
