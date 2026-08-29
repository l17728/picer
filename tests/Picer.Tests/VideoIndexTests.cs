using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-video-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;

    public VideoIndexTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "video.db"));
    }

    [Fact]
    public async Task VideoIndex_FindsSupportedFilesWithoutModifyingThem()
    {
        _database.AddSource(_root);
        var video = Path.Combine(_root, "family.MP4");
        File.WriteAllBytes(video, [1, 2, 3, 4]);
        var before = File.ReadAllBytes(video);
        var repository = new VideoRepository(_database);

        var count = await new VideoIndexService(_database, repository).ScanLocalSourcesAsync();

        Assert.Equal(1, count);
        Assert.Equal(video, Assert.Single(repository.GetAll()).Path);
        Assert.Equal(before, File.ReadAllBytes(video));
        var record = Assert.Single(repository.GetAll());
        repository.SaveTranscript(record.Id, "base-q5", "{\"text\":\"孩子在九寨沟唱歌\"}");
        Assert.Equal(record.Id, Assert.Single(repository.Search("唱歌")).Id);
    }

    [Fact]
    public void Database_HasVersionNineVideoTables()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "video.db")}");
        connection.Open();
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal(19L, (long)version.ExecuteScalar()!);
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('videos','video_keyframes','video_transcripts')";
        Assert.Equal(3L, (long)tables.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("a.mp4")][InlineData("a.MOV")][InlineData("a.m2ts")]
    public void VideoFormats_AcceptsCommonFamilyVideoFormats(string path) => Assert.True(VideoFormats.IsSupported(path));

    public void Dispose() { _database.Dispose(); try { Directory.Delete(_root, true); } catch { } }
}
