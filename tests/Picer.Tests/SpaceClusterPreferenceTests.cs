using Picer.Core.Data;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class SpaceClusterPreferenceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MigrationBindsLegacyThresholdOnlyWhenItsHistoricalModelIsUnambiguous(int modelCount)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-model-preference-migration-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"picer.db");
        try
        {
            using(var seed=new PicerDatabase(path))
            {
                var source=seed.AddSource(root);
                for(var index=0;index<modelCount;index++)
                {
                    var image=new Picer.Core.Models.ImageRecord { Id=index+1,SourceId=source.Id,Path=Path.Combine(root,$"{index}.jpg"),
                        FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow };
                    seed.UpsertMirroredImage(image);
                    new Picer.Core.SemanticSearch.ClipSemanticRepository(seed).Upsert(image,"model-"+index,new float[]{1,0});
                }
                seed.SaveClusterPreference(false,0.79f);
                using var downgrade=seed.Command("DROP TABLE semantic_cluster_model_recommendations; PRAGMA user_version=67");downgrade.ExecuteNonQuery();
            }
            using var upgraded=new PicerDatabase(path);
            var settings=new ContentSemanticClusterService(upgraded).GetSettingsForModel("model-0");
            Assert.Equal(modelCount!=1,settings.Automatic);
            Assert.Equal(modelCount==1?0.79f:0.82f,settings.ManualThreshold);
            Assert.False(new ContentSemanticClusterService(upgraded).GetSettingsForModel(null).Automatic);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    [Fact]
    public void ModelPreferencesRemainIndependentAcrossAnAuthoritativeRoundTrip()
    {
        using var source=new PicerDatabase(":memory:");using var remote=new PicerDatabase(":memory:");
        source.SaveClusterPreference(false,0.75f,modelVersion:"model-a");
        source.SaveClusterPreference(false,0.91f,modelVersion:"model-b");
        var edits=source.GetMetadataEdits();Assert.Equal(2,edits.Count);
        Assert.NotEqual(edits[0].Field,edits[1].Field);
        var accepted=remote.AcceptMetadataEdits(edits);source.MergeMetadataEdits(accepted);
        foreach(var database in new[]{source,remote})
        {
            var service=new ContentSemanticClusterService(database);
            Assert.Equal(0.75f,service.GetSettingsForModel("model-a").EffectiveThreshold);
            Assert.Equal(0.91f,service.GetSettingsForModel("model-b").EffectiveThreshold);
            Assert.True(service.GetSettingsForModel("model-c").Automatic);
            Assert.True(service.GetSettings().Automatic); // No model: legacy preferences remain separate.
        }
        Assert.Throws<ArgumentException>(()=>PicerDatabase.ValidateMetadataEdit(edits[0] with { Value=edits[1].Value }));
    }

    [Fact]
    public void DefaultsDoNotBecomeEditsAndOnlyUserSettingsTravelBetweenLibraries()
    {
        using var source=new PicerDatabase(":memory:"); using var server=new PicerDatabase(":memory:"); using var other=new PicerDatabase(":memory:");
        var service=new ContentSemanticClusterService(source);
        Assert.Empty(source.GetMetadataEdits()); Assert.True(service.GetSettings().Automatic);
        service.SaveSettings(false,0.75f);
        var accepted=server.AcceptMetadataEdits(source.GetMetadataEdits()); source.MergeMetadataEdits(accepted);
        using (var recommendation=other.Command("UPDATE semantic_cluster_settings SET recommended_threshold=0.91,recommendation_fingerprint='other-library' WHERE id=1")) recommendation.ExecuteNonQuery();
        other.MergeMetadataEdits(accepted);
        using var query=other.Command("SELECT automatic,manual_threshold,recommended_threshold,recommendation_fingerprint FROM semantic_cluster_settings WHERE id=1");
        using var row=query.ExecuteReader(); Assert.True(row.Read()); Assert.False(row.GetBoolean(0)); Assert.Equal(0.75f,row.GetFloat(1));
        Assert.Equal(0.91f,row.GetFloat(2)); Assert.Equal("other-library",row.GetString(3));
        Assert.Equal(Assert.Single(accepted).ServerVersion,service.GetSettings().Version);
    }

    [Fact]
    public void StaleRemoteSettingsKeepCurrentValuesAndPreserveTheProposalForReview()
    {
        using var database=new PicerDatabase(":memory:"); var service=new ContentSemanticClusterService(database);
        var initial=service.GetAuthoritativeSettings();
        Assert.NotNull(initial.Epoch);
        Assert.Throws<InvalidOperationException>(()=>service.SaveSettings(false,0.75f,true,initial.Version,expectedEpoch:Guid.NewGuid().ToString("N")));
        Assert.Empty(database.GetMetadataEdits());
        var saved=service.SaveSettings(false,0.75f,true,initial.Version);
        Assert.True(saved.Version>initial.Version);
        Assert.Throws<InvalidOperationException>(()=>service.SaveSettings(true,0.90f,true,initial.Version));
        Assert.False(service.GetSettings().Automatic); Assert.Equal(0.75f,service.GetSettings().ManualThreshold);
        var snapshot=database.GetMetadataConflictSnapshot(); var conflict=Assert.Single(snapshot.Reviews).Conflict;
        Assert.Equal("照片空间 · 动态分类设置",Assert.Single(snapshot.Reviews).Title);
        database.ResolveMetadataEditConflict(conflict.OperationId,true,conflict.Current.ServerVersion,conflict.Current.OperationId,true,snapshot.Epoch);
        Assert.True(service.GetSettings().Automatic); Assert.Equal(0.90f,service.GetSettings().ManualThreshold);
    }

    [Fact]
    public void OpenLocalWindowCannotOverwriteANewerOfflinePreferenceWithTheSameBase()
    {
        using var database=new PicerDatabase(":memory:"); var service=new ContentSemanticClusterService(database);
        var shown=service.SaveSettings(false,0.75f);
        service.SaveSettings(true,0.88f);
        Assert.Throws<InvalidOperationException>(()=>service.SaveSettings(false,0.90f,expectedVersion:shown.Version,expectedOperationId:shown.OperationId));
        Assert.True(service.GetSettings().Automatic); Assert.Equal(0.88f,service.GetSettings().ManualThreshold);
        Assert.Single(database.GetMetadataEditConflicts());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Automatic\":true,\"ManualThreshold\":0.2}")]
    [InlineData("{\"Automatic\":true,\"ManualThreshold\":0.8,\"device\":\"other\"}")]
    [InlineData("{\"Automatic\":\"true\",\"ManualThreshold\":0.8}")]
    public void InvalidPreferencesCannotEnterTheMetadataProtocol(string json)
    {
        using var database=new PicerDatabase(":memory:");
        Assert.Throws<ArgumentException>(()=>database.AcceptMetadataEdits([new("space-setting",1,"clustering",json,DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"))]));
        Assert.Empty(database.GetMetadataEdits());
    }

    [Fact]
    public void MigrationPreservesExistingCustomizationAsAnUnacknowledgedPreference()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-preference-migration-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"picer.db");
        try
        {
            using (var seed=new PicerDatabase(path))
            using (var downgrade=seed.Command("DROP TABLE sync_video_content_identities; UPDATE semantic_cluster_settings SET automatic=0,manual_threshold=0.79 WHERE id=1; PRAGMA user_version=64;")) downgrade.ExecuteNonQuery();
            using var upgraded=new PicerDatabase(path);
            var edit=Assert.Single(upgraded.GetMetadataEdits()); Assert.Equal("space-setting",edit.Kind); Assert.Equal(0,edit.ServerVersion);
            var settings=new ContentSemanticClusterService(upgraded).GetAuthoritativeSettings();
            Assert.False(settings.Automatic); Assert.Equal(0.79f,settings.ManualThreshold); Assert.True(settings.Version>0);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
