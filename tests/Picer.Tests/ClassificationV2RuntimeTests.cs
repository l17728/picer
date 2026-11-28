using Picer.Core.Classification;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class ClassificationV2RuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-classification-v2-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly ClassificationRepository _repo;
    private readonly long _source;
    private static string Request() => Guid.NewGuid().ToString("N");
    private static ClassificationConfiguration Config => new(true, "fixture-model", "local", false, 100);
    public ClassificationV2RuntimeTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "index.db")); _repo = new(_db);
        _source = _db.AddSource(_root).Id;
        _repo.Configure(Config);
    }
    private long Add(string name)
    {
        var image = new ImageRecord { SourceId = _source, Path = Path.Combine(_root, name), FileSize = 100,
            ModifiedUtc = DateTime.UtcNow, Width = 600, Height = 400, IndexedUtc = DateTime.UtcNow };
        _db.UpsertImage(image);
        return _db.GetImages(_source).Single(i => i.Path == image.Path).Id;
    }
    private ClassificationResult Result(ClassificationLease lease, params string[] labels) => new("classification-result-v1", lease.Fingerprint,
        _repo.Taxonomy().Labels.Where(l => l.Assignable && l.Active).Select(l => new ClassificationLabelEvaluation(l.Id,
            labels.Contains(l.Id) ? "supported" : "absent", labels.Contains(l.Id) ? ["image:current"] : [])).ToArray(), []);
    private ClassificationJob Start()
    { var plan = _repo.Plan(Request(), Config); return _repo.Start(plan.Id, Request()); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrSaturatedLabelGroupsRetryBeforePublishing(bool saturated)
    {
        Add("review.jpg");
        var job = Start();
        var prompts = new List<string>();
        var model = new ClassificationModel((_, _) => Task.FromResult(new byte[] { 255, 216, 255, 217 }), (_, payload, _) =>
        {
            using var request = System.Text.Json.JsonDocument.Parse(payload);
            var messages = request.RootElement.GetProperty("messages");
            var parts = messages[1].GetProperty("content");
            prompts.Add(parts[parts.GetArrayLength() - 1].GetProperty("text").GetString()!);
            using var input = System.Text.Json.JsonDocument.Parse(messages[1].GetProperty("content")[0].GetProperty("text").GetString()!);
            var fingerprint = input.RootElement.GetProperty("inputFingerprint").GetString();
            var response = prompts.Count == 1
                ? System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-indexed-v1", inputFingerprint = fingerprint,
                    evaluatedAll = true, supported = saturated ? Enumerable.Range(0, input.RootElement.GetProperty("labels").GetArrayLength()).ToArray() : new[] { 0 },
                    uncertain = saturated ? Array.Empty<int>() : new[] { 0 }, conflicting = Array.Empty<int>(), proposals = Array.Empty<object>() })
                : System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-sparse-v1", inputFingerprint = fingerprint,
                    evaluatedAll = true, labels = new[] { new { labelId = "scene.outdoor", support = "supported", evidenceIds = new[] { "image:current" } } },
                    proposals = Array.Empty<object>() });
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = response } } } }));
        });
        var worker = new ClassificationWorker(_db.OpenIndependentConnection, model);
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(0, _repo.Job(job.Id).Succeeded);
        Assert.Equal(1, _repo.Job(job.Id).Pending);
        // Advance the retry window without sleeping or changing its production limit.
        using (var ready = _db.Command("UPDATE classification_v2_work SET retry_after=NULL")) ready.ExecuteNonQuery();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, prompt => Assert.Contains("classification-sparse-v1", prompt));
        Assert.All(prompts, prompt => Assert.Contains("cue", prompt));
        Assert.DoesNotContain("上次结果", prompts[0]);
        Assert.Contains("上次结果", prompts[1]);
        Assert.Equal(1, _repo.Job(job.Id).Succeeded);
        Assert.Equal(2, _repo.Job(job.Id).UsedCalls);
        Assert.Equal(new[] { "scene.outdoor" }, Assert.Single(_repo.Browse(new(new([], []))).Photos).SuggestedIds);
    }

    [Fact]
    public void RetrievalUsesCurrentMachineLabelsOnlyWhenSuggestionsAllowed()
    {
        var id = Add("recall.jpg");
        var job = Start();
        var lease = _repo.Claim()!;
        Assert.Equal(job.Id, lease.JobId);
        Assert.True(_repo.Publish(lease, Result(lease, "scene.outdoor")));
        var replica = _repo.ExportReplica();
        Assert.Empty(new Picer.Core.Search.ClassificationEvidenceIndex(replica, false).Match(_db, ["户外"]));
        Assert.Contains(id, new Picer.Core.Search.ClassificationEvidenceIndex(replica, true).Match(_db, ["户外"]).Keys);
        using var change = _db.Command("UPDATE images SET file_size=file_size+1 WHERE id=$id");
        change.Parameters.AddWithValue("$id", id); change.ExecuteNonQuery();
        Assert.Empty(new Picer.Core.Search.ClassificationEvidenceIndex(_repo.ExportReplica(), true).Match(_db, ["户外"]));
    }

    [Fact]
    public void RetrievalIncludesValidLegacyAndDescriptionEvidenceWithoutPromotingItToConfirmed()
    {
        var id = Add("legacy-evidence.jpg");
        var replica = _repo.ExportReplica();
        replica = replica with { Photos = replica.Photos.Select(photo => photo with { LegacySuggestedIds = ["activity.cooking"] }).ToArray() };
        var result = new Picer.Core.Search.ClassificationEvidenceIndex(replica, true).Match(_db, ["烹饪"]);
        Assert.Contains(id, result.Keys);
        Assert.Equal("描述派生建议", Assert.Single(result[id]).Source);
        Assert.Empty(new Picer.Core.Search.ClassificationEvidenceIndex(replica, false).Match(_db, ["烹饪"]));
        replica = replica with { Photos = replica.Photos.Select(photo => photo with { ConflictLabelIds = ["activity.cooking"] }).ToArray() };
        Assert.Empty(new Picer.Core.Search.ClassificationEvidenceIndex(replica, true).Match(_db, ["烹饪"]));
    }

    [Fact]
    public void OldDescriptionSuggestionsRemainDistinctAndDisappearWhenEvidenceChanges()
    {
        var id = Add("described.jpg");
        var image = _db.GetImage(id)!;
        var descriptions = new Picer.Core.Vision.CloudVisionRepository(_db);
        descriptions.Save(image, new DescriptionAnalyzer(), "none", "test",
            new Picer.Core.Vision.CloudVisionResult("户外的花卉", ["户外"], [], ["花卉"], [], null, "none"));
        var projection = new Picer.Core.Vision.ClassificationProjectionService(_db);
        projection.ProcessNextBatch(projection.BeginOrResume().RunId);
        var replica = _repo.ExportReplica();
        var photo = Assert.Single(replica.Photos);
        Assert.Contains("scene.outdoor", photo.LegacySuggestedIds!);
        Assert.Contains("object.flower", photo.LegacySuggestedIds!);
        Assert.Empty(photo.SuggestedIds!);
        Assert.Empty(photo.ConfirmedIds!);
        Assert.Equal("unprocessed", photo.State);
        Assert.Equal(1, replica.Coverage.LegacySuggested);
        foreach (var includeSuggestions in new[] { true, false })
        foreach (var includeLegacy in new[] { true, false })
        {
            var request = new ClassificationBrowseRequest(new([], []), includeSuggestions, includeLegacy);
            var live = Assert.Single(_repo.Browse(request).Photos);
            var cached = Assert.Single(ClassificationRepository.BrowseReplica(replica, request).Photos);
            Assert.Equal(includeSuggestions && includeLegacy, live.LabelIds.Contains("object.flower"));
            Assert.Equal(live.LabelIds, cached.LabelIds);
        }
        descriptions.Save(image, new DescriptionAnalyzer(), "none", "test",
            new Picer.Core.Vision.CloudVisionResult("室内", ["室内"], [], [], [], null, "none"));
        Assert.Empty(Assert.Single(_repo.Browse(new(new([], []))).Photos).LegacySuggestedIds!);
    }

    [Fact]
    public void ConfirmedFaceDerivesOnlyPeopleAndHonorsRejectionAndArchival()
    {
        var id = Add("people.jpg");
        var people = new Picer.Core.People.PeopleService(_db);
        var person = people.Create("测试人物");
        using (var command = _db.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($id,'fixture',0,.1,.1,.3,.3,.99,$now); SELECT last_insert_rowid();
            """))
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            people.ConfirmFace((long)command.ExecuteScalar()!, person.Id);
        }
        var request = new ClassificationBrowseRequest(new([], []), false, false);
        var page = _repo.Browse(request);
        var photo = Assert.Single(page.Photos);
        Assert.Contains("subject.people", photo.LabelIds);
        Assert.Contains("person.confirmed." + person.Id, photo.LabelIds);
        Assert.DoesNotContain(_repo.Taxonomy().Labels, l => l.Id.StartsWith("person.confirmed.", StringComparison.Ordinal));
        Assert.Equal(1, _repo.Browse(new(new([new(ClassificationMatchMode.All, ["person.confirmed." + person.Id])], []))).Matched);
        Assert.Empty(photo.ConfirmedIds!);
        Assert.Equal(0, page.Coverage.Suggested);
        Assert.Equal(1, page.Coverage.EvidenceDerived);
        Assert.Equal(photo.LabelIds, Assert.Single(ClassificationRepository.BrowseReplica(_repo.ExportReplica(), request).Photos).LabelIds);
        var beforeRename = _repo.ExportReplica();
        using (var rename = _db.Command("UPDATE people SET display_name=$name WHERE id=$id"))
        {
            rename.Parameters.AddWithValue("$name", "更新后的测试名称"); rename.Parameters.AddWithValue("$id", person.Id); rename.ExecuteNonQuery();
        }
        var afterRename = _repo.ExportReplica();
        Assert.NotEqual(beforeRename.Snapshot, afterRename.Snapshot);
        Assert.Equal("更新后的测试名称", afterRename.Taxonomy.Labels.Single(l => l.Id == "person.confirmed." + person.Id).Name);
        _repo.Decide(new(Request(), _repo.Epoch, "subject.people", "reject", new Dictionary<long, string> { [id] = photo.ContentIdentity },
            new Dictionary<long, string> { [id] = photo.OwnerId }, page.Taxonomy.Revision,
            new Dictionary<long, string> { [id] = photo.ManualFingerprint! }));
        Assert.Empty(Assert.Single(_repo.Browse(request).Photos).LabelIds);
        using var archive = _db.Command("UPDATE detected_faces SET is_archived=1 WHERE image_id=$id");
        archive.Parameters.AddWithValue("$id", id); archive.ExecuteNonQuery();
        Assert.Equal(0, _repo.Browse(request).Coverage.WithPeople);
    }

    private sealed class DescriptionAnalyzer : Picer.Core.Vision.ICloudVisionAnalyzer
    {
        public string Provider => "fixture";
        public string ModelVersion => "fixture";
        public string PromptVersion => "fixture";
        public Task<Picer.Core.Vision.CloudVisionResult> AnalyzeAsync(Picer.Core.Vision.CloudVisionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void BrowseCacheReusesSnapshotAndInvalidatesAfterExternalCommit()
    {
        Add("first.jpg");
        using var cache = new ClassificationBrowseCache(_db);
        var first = cache.ExportReplica();
        Assert.Same(first, cache.ExportReplica());
        Assert.False(cache.Browse(new(new([], []))).IsOffline);
        Add("second.jpg");
        var second = cache.ExportReplica();
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Coverage.Total);
        Assert.Equal(2, cache.Browse(new(new([], []))).Matched);
        Assert.Same(second, cache.ExportReplica());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => cache.ExportReplica(cancelled.Token));
        Assert.Same(second, cache.ExportReplica());
    }

    [Fact]
    public void BrowseCacheInvalidatesPublishedLabelsAndRejectsOldPagination()
    {
        Add("photo.jpg");
        using var cache = new ClassificationBrowseCache(_db);
        var before = cache.Browse(new(new([], [])));
        Start();
        var lease = _repo.Claim()!;
        Assert.True(_repo.Publish(lease, Result(lease, "subject.people", "scene.forest", "scene.forest.conifer")));
        var after = cache.Browse(new(new([], [])));
        Assert.Equal(1, after.Counts["scene.forest"]);
        var graph = new ClassificationTaxonomy(after.Taxonomy.Labels, after.Taxonomy.Edges);
        foreach (var label in graph.Labels.Values)
            Assert.Equal(label.Active ? after.Photos.Count(photo => photo.LabelIds.Any(graph.Expand(label.Id).Contains)) : 0,
                after.Counts[label.Id]);
        var confirmedOnly = cache.Browse(new(new([], []), IncludeSuggestions: false, IncludeLegacy: false));
        Assert.All(confirmedOnly.Counts.Values, count => Assert.Equal(0, count));
        Assert.NotEqual(before.Snapshot, after.Snapshot);
        Assert.Throws<InvalidOperationException>(() => cache.Browse(new(new([], []), Snapshot: before.Snapshot)));
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.ExportReplica());
    }

    [Fact]
    public void BrowseCountsIncludeDeduplicatedDimensionAggregates()
    {
        Add("labelled.jpg");
        Add("untouched.jpg");
        using var cache = new ClassificationBrowseCache(_db);
        Start();
        var lease = _repo.Claim()!;
        Assert.True(_repo.Publish(lease, Result(lease, "subject.people", "scene.forest", "scene.forest.conifer")));
        var after = cache.Browse(new(new([], [])));
        Assert.Equal(1, after.Counts["scene.forest"]);
        Assert.Equal(1, after.Counts["scene.forest.conifer"]);
        var scene = after.Taxonomy.Labels.Single(x => x.Id == "scene.forest");
        // forest and its child both label the same photo; the dimension aggregate must count that
        // photo once, and a dimension without any photo must not appear with a positive count.
        Assert.Equal(1, after.Counts[ClassificationCountKeys.Dimension(scene.Dimension)]);
        Assert.Equal(0, after.Counts.GetValueOrDefault(ClassificationCountKeys.NamedPeople));
        var labelled = new[] { "subject.people", "scene.forest", "scene.forest.conifer" };
        var labelledDimensions = new HashSet<string>(after.Taxonomy.Labels.Where(x => labelled.Contains(x.Id)).Select(x => x.Dimension), StringComparer.Ordinal);
        Assert.Subset(labelledDimensions, new HashSet<string>(new[] { scene.Dimension }));
        var untouchedDimension = after.Taxonomy.Labels.Select(x => x.Dimension).Distinct(StringComparer.Ordinal)
            .FirstOrDefault(dimension => !labelledDimensions.Contains(dimension));
        if (untouchedDimension is not null)
            Assert.Equal(0, after.Counts.GetValueOrDefault(ClassificationCountKeys.Dimension(untouchedDimension)));
        var confirmedOnly = cache.Browse(new(new([], []), IncludeSuggestions: false));
        Assert.All(confirmedOnly.Counts.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public void FullLifecyclePersistsSuccessAndUnchangedScanSkipsModelWork()
    {
        Add("photo.jpg");
        var job = Start();
        var lease = Assert.IsType<ClassificationLease>(_repo.Claim());
        Assert.True(_repo.Publish(lease, Result(lease, "subject.people", "scene.forest")));
        Assert.False(_repo.Publish(lease, Result(lease, "subject.people")));
        Assert.Equal("completed", _repo.Job(job.Id).State);
        Assert.Empty(_repo.Plan(Request(), Config).Items);
        var page = _repo.Browse(new(new([], [])));
        Assert.Equal(1, page.Coverage.Suggested);
        Assert.Equal(1, page.Counts["scene.forest"]);
        Assert.Equal(1, page.Matched);
        using var reopened = _db.OpenIndependentConnection();
        Assert.Equal(1, new ClassificationRepository(reopened).Job(job.Id).Succeeded);
    }

    [Fact]
    public void IncrementalPlanPagesContextsAndRebuildsInputsWhenStarting()
    {
        for (var i = 0; i < 205; i++) Add($"page-{i:D3}.jpg");

        var plan = _repo.Plan(Request(), Config);
        Assert.Equal(205, plan.Total);
        Assert.Equal(205, plan.Items.Count);
        Assert.All(plan.Items, item => Assert.Null(item.Input));

        var job = _repo.Start(plan.Id, Request());
        Assert.Equal(205, job.Total);
        var lease = Assert.IsType<ClassificationLease>(_repo.Claim());
        Assert.NotEmpty(lease.Input.Metadata);
    }

    [Theory]
    [InlineData("classification_output_invalid", true)]
    [InlineData("classification_output_incomplete", true)]
    [InlineData("classification_evidence_invalid", true)]
    [InlineData("classification_proposal_invalid", true)]
    [InlineData("classification_media_unavailable", false)]
    public void ModelOutputValidationFailuresAreRetryableWithinTheExistingAttemptLimit(string code, bool expected)
    {
        Assert.Equal(expected, ClassificationWorker.IsRetryableFailure(new InvalidDataException(code)));
    }

    [Theory]
    [InlineData("media_agent_offline", true)]
    [InlineData("media_agent_disconnected", true)]
    [InlineData("media_channel_busy", true)]
    [InlineData("media_agent_selection_required", false)]
    [InlineData("media_preview_too_large", false)]
    public void MediaReadRetriesOnlyTemporaryFailures(string code, bool expected)
    {
        Assert.Equal(expected, ClassificationWorker.IsRetryableFailure(new IOException(code)));
    }

    [Fact]
    public void MalformedModelJsonIsRetryableWithinTheExistingAttemptLimit()
    {
        Assert.True(ClassificationWorker.IsRetryableFailure(new System.Text.Json.JsonException()));
    }

    [Fact]
    public void IncompleteModelOutputNeverAdvancesSuccess()
    {
        Add("photo.jpg"); Start(); var lease = _repo.Claim()!;
        Assert.Throws<InvalidDataException>(() => _repo.Publish(lease, new("classification-result-v1", lease.Fingerprint, [], [])));
        _repo.Fail(lease, "classification_output_incomplete", false);
        Assert.Empty(_repo.Plan(Request(), Config).Items);
        Assert.Single(_repo.Plan(Request(), Config, retryFailures: true).Items);
        Assert.Equal(1, _repo.Browse(new(new([], []))).Coverage.Failed);
    }

    [Fact]
    public void EmptyCompleteOutputIsAnalyzedUnclassifiedNotFailure()
    {
        Add("photo.jpg"); Start(); var lease = _repo.Claim()!;
        _repo.Publish(lease, Result(lease));
        var coverage = _repo.Browse(new(new([], []))).Coverage;
        Assert.Equal(1, coverage.AnalyzedUnclassified); Assert.Equal(0, coverage.Failed);
        Assert.Equal(1, coverage.WithoutContent);
    }

    [Fact]
    public void ChangedMediaRejectsStalePlanAndInFlightPublication()
    {
        var id = Add("photo.jpg");
        var plan = _repo.Plan(Request(), Config);
        using (var update = _db.Command("UPDATE images SET file_size=file_size+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => _repo.Start(plan.Id, Request()));
        Start(); var lease = _repo.Claim()!;
        using (var update = _db.Command("UPDATE images SET file_size=file_size+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery(); }
        Assert.False(_repo.Publish(lease, Result(lease, "subject.people")));
        Assert.Equal(0, _repo.Browse(new(new([], []))).Coverage.Suggested);
    }

    [Fact]
    public void RetryFailedReopensOnlyFailedItemsAndKeepsCallBudget()
    {
        Add("success.jpg"); Add("retry.jpg");
        var job = Start();
        var first = _repo.Claim()!;
        Assert.True(_repo.Publish(first, Result(first)));
        var failed = _repo.Claim()!;
        _repo.Fail(failed, "classification_json_invalid", false);
        _repo.Control(job.Id, "retry-failed");
        _repo.Control(job.Id, "retry-failed");
        var retry = _repo.Claim()!;
        Assert.Equal(failed.ImageId, retry.ImageId);
        Assert.Equal(job.Id, retry.JobId);
        Assert.True(_repo.Publish(retry, Result(retry)));
        Assert.Null(_repo.Claim());
        Assert.Equal(2, _repo.Job(job.Id).Succeeded);
        Assert.Equal(3, _repo.Job(job.Id).UsedCalls);
    }

    [Fact]
    public void PauseCancelAndBudgetAreDurable()
    {
        Add("a.jpg"); Add("b.jpg"); _repo.Configure(Config with { MaximumCalls = 1 });
        var plan = _repo.Plan(Request(), _repo.Configuration()!); var job = _repo.Start(plan.Id, Request());
        _repo.Control(job.Id, "pause"); Assert.Null(_repo.Claim());
        _repo.Control(job.Id, "resume"); var lease = _repo.Claim()!;
        _repo.Publish(lease, Result(lease)); Assert.Null(_repo.Claim());
        Assert.Equal("paused_budget", _repo.Job(job.Id).State);
        _repo.Control(job.Id, "cancel"); Assert.Equal("cancelled", _repo.Job(job.Id).State);
    }

    [Fact]
    public void BrowsingReadOnlyDoesNotSeedOrRepairAndCountsAreNotPageCounts()
    {
        for (var i = 0; i < 65; i++) Add($"{i}.jpg");
        using var read = _db.OpenReadOnlyConnection();
        var repository = new ClassificationRepository(read);
        var page = repository.Browse(new(new([], []), PageSize: 20));
        Assert.Equal(65, page.Coverage.Total); Assert.Equal(20, page.Photos.Count); Assert.Equal(65, page.Matched);
        var next = repository.Browse(new(new([], []), PageSize: 20, Cursor: page.NextCursor, Snapshot: page.Snapshot));
        Assert.Empty(page.Photos.Select(p => p.ImageId).Intersect(next.Photos.Select(p => p.ImageId)));
        Add("new.jpg");
        Assert.Throws<InvalidOperationException>(() => repository.Browse(new(new([], []), Cursor: page.NextCursor, Snapshot: page.Snapshot)));
    }

    [Fact]
    public void AcceptAndMatchQueuesOnlyReviewedExamplesAndPublishesAfterEvaluation()
    {
        Add("example.jpg"); Start(); var original = _repo.Claim()!;
        var draft = new ClassificationProposalDraft("露天茶席", "activity", "户外茶席活动", "单个茶杯不足以确认", ["nav.activity"], ["image:current"]);
        Assert.True(_repo.Publish(original, Result(original) with { Proposals = [draft] }));
        Add("other.jpg");
        var proposal = Assert.Single(_repo.Proposals());
        var review = new ClassificationProposalReview(Request(), proposal.Id, proposal.Revision, _repo.Taxonomy().Revision, "accept");
        var job = _repo.AcceptAndMatch(review);
        Assert.Equal(job.Id, _repo.AcceptAndMatch(review).Id);
        Assert.Equal(1, job.Total);
        var label = "dynamic." + proposal.Id;
        Assert.Equal(0, _repo.Browse(new(new([], []))).Counts.GetValueOrDefault(label));
        var lease = _repo.Claim()!;
        Assert.Equal(original.ImageId, lease.ImageId);
        Assert.True(_repo.Publish(lease, Result(lease, label)));
        Assert.Equal(1, _repo.Browse(new(new([], []))).Counts[label]);
        Assert.Null(_repo.Claim());
    }

    [Fact]
    public void ProposalAcceptanceChangesDiscoveryAndNeverCopiesRepresentativeAssignments()
    {
        Add("photo.jpg"); Start(); var lease = _repo.Claim()!;
        var proposal = new ClassificationProposalDraft("露天茶席", "activity", "户外布置并参与茶席活动", "单个茶杯不足以确认", ["nav.activity"], ["image:current"]);
        _repo.Publish(lease, Result(lease) with { Proposals = [proposal] });
        var saved = Assert.Single(_repo.Proposals());
        var review = new ClassificationProposalReview(Request(), saved.Id, saved.Revision, _repo.Taxonomy().Revision, "accept");
        var taxonomy = _repo.Review(review);
        Assert.Equal(taxonomy.Revision, _repo.Review(review).Revision);
        Assert.Equal(2, taxonomy.DiscoveryRevision);
        Assert.Single(_repo.Plan(Request(), Config).Items);
        Assert.Equal(0, _repo.Browse(new(new([], []))).Coverage.Suggested);
    }

    [Fact]
    public void ConcurrentPlansDoNotCreateTwoActiveLeasesAndCancellationRejectsResponse()
    {
        Add("photo.jpg"); var first = Start(); var lease = _repo.Claim()!;
        var second = Start(); Assert.Null(_repo.Claim());
        _repo.Control(first.Id, "cancel"); Assert.False(_repo.Publish(lease, Result(lease)));
        var successor = _repo.Claim()!; Assert.Equal(second.Id, successor.JobId);
    }

    [Fact]
    public void SameFingerprintWaitingTaskReusesSuccessWithoutSecondModelCall()
    {
        Add("photo.jpg"); Start(); var lease = _repo.Claim()!;
        var second = Start(); _repo.Publish(lease, Result(lease));
        Assert.Null(_repo.Claim());
        Assert.Equal(0, _repo.Job(second.Id).UsedCalls);
        Assert.Equal("completed", _repo.Job(second.Id).State);
    }

    [Fact]
    public void InFlightInputChangeCreatesPersistentSuccessor()
    {
        var id = Add("photo.jpg"); var job = Start(); var lease = _repo.Claim()!;
        using (var update = _db.Command("UPDATE images SET width=width+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery(); }
        Assert.False(_repo.Publish(lease, Result(lease)));
        var successor = _repo.Claim()!;
        Assert.Equal(job.Id, successor.JobId); Assert.NotEqual(lease.Fingerprint, successor.Fingerprint);
        Assert.True(_repo.Publish(successor, Result(successor)));
    }

    [Fact]
    public void FailedObsoleteCallAlsoLeavesSuccessor()
    {
        var id = Add("photo.jpg"); var job = Start(); var lease = _repo.Claim()!;
        using (var update = _db.Command("UPDATE images SET width=width+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery(); }
        _repo.Fail(lease, "classification_output_invalid", false);
        var successor = _repo.Claim()!;
        Assert.Equal(job.Id, successor.JobId); Assert.NotEqual(lease.Fingerprint, successor.Fingerprint);
    }

    [Fact]
    public void ClusterRejectsChangedEvidenceBeforeCallingModel()
    {
        var a = Add("a.jpg"); var b = Add("b.jpg");
        _repo.PlanCluster(new(Request(), [a, b]));
        using (var update = _db.Command("UPDATE images SET width=width+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", a); update.ExecuteNonQuery(); }
        Assert.Null(_repo.ClaimCluster());
    }

    [Fact]
    public void ClusterOnlyRecordsActuallySupportedMembersAndStaleProposalCannotBeAccepted()
    {
        var ids = Enumerable.Range(0, 8).Select(i => Add(i + ".jpg")).ToArray();
        var task = _repo.PlanCluster(new(Request(), ids)); var claim = _repo.ClaimCluster()!.Value;
        var draft = new ClassificationProposalDraft("园中茶席", "activity", "园中参与茶席", "孤立茶杯不足以确认", ["nav.activity"], ["image:current"]);
        _repo.CompleteCluster(task, claim.Lease, new("classification-cluster-v1", ids, [new(draft, ids.Take(2).ToArray())]));
        var proposal = Assert.Single(_repo.Proposals()); Assert.Equal(2, proposal.ImageIds.Count);
        using (var update = _db.Command("UPDATE images SET width=width+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", ids[0]); update.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => _repo.Review(new(Request(), proposal.Id, proposal.Revision, _repo.Taxonomy().Revision, "accept")));
    }

    [Fact]
    public void ReplicaReadbackPreservesIndependentBaseline()
    {
        Add("photo.jpg"); var replica = _repo.ExportReplica();
        _repo.SaveReplica("test-space", replica);
        Assert.Equal(replica.Snapshot, _repo.LoadReplica("test-space")!.Snapshot);
        Assert.Equal(1, _repo.LoadReplica("test-space")!.Coverage.Total);
        var page = ClassificationRepository.BrowseReplica(replica, new(new([], [])));
        Assert.True(page.IsOffline); Assert.Equal(1, page.Matched);
        Assert.Equal(_repo.Epoch, page.Epoch);
    }

    [Fact]
    public void ManualDecisionChecksOwnerAndSurvivesMachineReclassification()
    {
        var id = Add("photo.jpg"); Start(); var lease = _repo.Claim()!;
        _repo.Publish(lease, Result(lease, "subject.people"));
        var page = _repo.Browse(new(new([], [])));
        Assert.Equal(_repo.Epoch, page.Epoch);
        var photo = Assert.Single(page.Photos);
        var decision = new ClassificationDecision(Request(), page.Epoch!, "subject.people", "confirm",
            new Dictionary<long, string> { [id] = photo.ContentIdentity }, new Dictionary<long, string> { [id] = photo.OwnerId },
            page.Taxonomy.Revision, new Dictionary<long, string> { [id] = photo.ManualFingerprint! });
        Assert.Equal(1, _repo.Decide(decision)); Assert.Equal(0, _repo.Decide(decision));
        Assert.Equal(1, _repo.Browse(new(new([], []), IncludeSuggestions: false)).Coverage.Confirmed);
        var revised = Config with { PromptVersion = "revised-evidence-prompt" };
        _repo.Configure(revised);
        var repair = _repo.Plan(Request(), revised);
        Assert.Equal(id, Assert.Single(repair.Items).ImageId);
        var repairJob = _repo.Start(repair.Id, Request());
        var repairLease = _repo.Claim()!;
        Assert.Equal(repairJob.Id, repairLease.JobId);
        Assert.True(_repo.Publish(repairLease, Result(repairLease, "scene.outdoor")));
        var repaired = Assert.Single(_repo.Browse(new(new([], []))).Photos);
        Assert.Contains("subject.people", repaired.ConfirmedIds!);
        Assert.Equal(new[] { "scene.outdoor" }, repaired.SuggestedIds);
        using (var update = _db.Command("UPDATE library_identity SET library_id='different-library' WHERE id=1"))
        { update.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => _repo.Decide(decision with { OperationId = Request(), Decision = "reject" }));
    }

    [Fact]
    public void OfflineEditsKeepReceiptAndReadbackStagesSeparate()
    {
        var id = Request(); _repo.QueueEdit("space", "decisions", id, "{}");
        _repo.QueueEdit("space", "decisions", id, "{}");
        Assert.Single(_repo.Outbox("space"));
        Assert.Throws<InvalidOperationException>(() => _repo.QueueEdit("space", "decisions", id, "{\"different\":1}"));
        _repo.MarkEdit(id, "acknowledged"); Assert.Equal("acknowledged", Assert.Single(_repo.Outbox("space")).State);
        _repo.MarkEdit(id, "completed"); Assert.Empty(_repo.Outbox("space"));
    }

    [Fact]
    public void OfflineReviewCannotOverwriteNewerManualDecisionAndReplayIsIdempotent()
    {
        var id = Add("offline.jpg");
        ClassificationDecision Observe(string action)
        {
            var page = _repo.Browse(new(new([], [])));
            var photo = Assert.Single(page.Photos);
            return new(Request(), _repo.Epoch, "subject.people", action,
                new Dictionary<long, string> { [id] = photo.ContentIdentity },
                new Dictionary<long, string> { [id] = photo.OwnerId }, page.Taxonomy.Revision,
                new Dictionary<long, string> { [id] = photo.ManualFingerprint! });
        }
        var offline = Observe("confirm");
        var reject = Observe("reject");
        Assert.Equal(1, _repo.Decide(reject));
        Assert.Throws<InvalidOperationException>(() => _repo.Decide(offline));
        Assert.Equal(0, _repo.Decide(reject));
        Assert.Empty(Assert.Single(_repo.Browse(new(new([], []))).Photos).ConfirmedIds!);
        var restore = Observe("restore");
        Assert.Equal(1, _repo.Decide(restore));
        Assert.Throws<InvalidOperationException>(() => _repo.Decide(offline));
    }

    [Fact]
    public void OutboxPagesPastConflictsAndFreezesNewArrivals()
    {
        for (var i = 0; i < 205; i++)
        {
            var id = Request(); _repo.QueueEdit("space", "decisions", id, "{}"); _repo.MarkEdit(id, "conflict");
        }
        var pending = Request(); _repo.QueueEdit("space", "decisions", pending, "{}");
        using var snapshot = _repo.PendingSnapshot("space").GetEnumerator();
        Assert.True(snapshot.MoveNext());
        var late = Request(); _repo.QueueEdit("space", "decisions", late, "{}");
        var ids = new List<string> { snapshot.Current.OperationId };
        while (snapshot.MoveNext()) ids.Add(snapshot.Current.OperationId);
        Assert.Equal(206, ids.Count);
        Assert.Contains(pending, ids); Assert.DoesNotContain(late, ids);
        Assert.Contains(_repo.PendingSnapshot("space"), item => item.OperationId == late);
    }

    [Fact]
    public void OfflineStructureEditsAreBoundToOriginAndRoute()
    {
        var id = Request();
        _repo.QueueEdit("space", "taxonomy/edit", id, "{}");
        Assert.Equal("taxonomy/edit", Assert.Single(_repo.Outbox("space")).Route);
        Assert.Throws<InvalidOperationException>(() => _repo.QueueEdit("other-space", "taxonomy/edit", id, "{}"));
        Assert.Throws<InvalidOperationException>(() => _repo.QueueEdit("space", "proposals/review", id, "{}"));
    }

    [Fact]
    public void InvalidTaxonomyPlacementRollsBackWithoutChangingRevision()
    {
        var taxonomy = _repo.Taxonomy();
        Assert.Throws<ArgumentException>(() => _repo.EditTaxonomy(new(Request(), taxonomy.Revision,
            "scene.forest", BrowseParentIds: ["scene.forest"])));
        Assert.Equal(taxonomy.Revision, _repo.Taxonomy().Revision);
    }

    [Fact]
    public void MigrationReplayPreservesCompatibleRowsAndInputWatermark()
    {
        var path = Path.Combine(_root, "replay.db");
        long revision;
        using (var original = new PicerDatabase(path))
        {
            new ClassificationRepository(original).Configure(Config);
            using var read = original.Command("SELECT input_revision FROM classification_v2_revision WHERE id=1");
            revision = (long)read.ExecuteScalar()!;
            using var downgrade = original.Command("PRAGMA user_version=92"); downgrade.ExecuteNonQuery();
        }
        using var migrated = new PicerDatabase(path);
        Assert.Equal(Config, new ClassificationRepository(migrated).Configuration());
        using var watermark = migrated.Command("SELECT input_revision FROM classification_v2_revision WHERE id=1");
        Assert.Equal(revision, (long)watermark.ExecuteScalar()!);
        using var version = migrated.Command("PRAGMA user_version"); Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, version.ExecuteScalar());
    }

    [Fact]
    public async Task RevokedTaskDoesNotInvokeModel()
    {
        Add("photo.jpg"); var plan = _repo.Plan(Request(), Config); var job = _repo.Start(plan.Id, Request(), "revoked");
        var model = new CountingModel();
        var worker = new ClassificationWorker(_db.OpenIndependentConnection, model, new(_ => false, (_, _) => throw new UnauthorizedAccessException()));
        Assert.True(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(0, model.Calls); Assert.Equal("blocked_authorization", _repo.Job(job.Id).State);
        Assert.Equal(0, _repo.Job(job.Id).UsedCalls);
        _repo.Control(job.Id, "resume", "new-authorization");
        Assert.Equal("new-authorization", _repo.Claim()!.Actor);
    }
    [Fact]
    public void TaxonomyRenamePreservesInferenceAndRetirementChangesDiscovery()
    {
        Add("photo.jpg"); Start(); var lease = _repo.Claim()!;
        _repo.Publish(lease, Result(lease, "scene.forest"));
        var taxonomy = _repo.Taxonomy();
        var edit = new ClassificationTaxonomyEdit(Request(), taxonomy.Revision, "scene.forest", "林地");
        var renamed = _repo.EditTaxonomy(edit);
        Assert.Equal(renamed.Revision, _repo.EditTaxonomy(edit).Revision);
        Assert.Equal(taxonomy.DiscoveryRevision, renamed.DiscoveryRevision);
        Assert.Empty(_repo.Plan(Request(), Config).Items);
        Assert.Throws<InvalidOperationException>(() => _repo.EditTaxonomy(edit with { OperationId = Request() }));
        var retired = _repo.EditTaxonomy(new(Request(), renamed.Revision, "scene.forest", Active: false));
        Assert.Equal(renamed.DiscoveryRevision + 1, retired.DiscoveryRevision);
        Assert.Single(_repo.Plan(Request(), Config).Items);
        var restored = _repo.EditTaxonomy(new(Request(), retired.Revision, "scene.forest", Active: true));
        Assert.True(restored.Labels.Single(l => l.Id == "scene.forest").Active);
    }

    [Fact]
    public void RevokedBatchReleasesAllPendingPhotosForAuthorizedSuccessor()
    {
        Add("one.jpg"); Add("two.jpg"); var original = Start();
        var revoked = _repo.Claim()!;
        _repo.BlockAuthorization(revoked, modelCalled: false);
        var successor = Start();
        for (var i = 0; i < 2; i++)
        {
            var lease = Assert.IsType<ClassificationLease>(_repo.Claim());
            Assert.Equal(successor.Id, lease.JobId);
            Assert.True(_repo.Publish(lease, Result(lease)));
        }
        Assert.Equal("blocked_authorization", _repo.Job(original.Id).State);
        Assert.Equal("completed", _repo.Job(successor.Id).State);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("restore")]
    public void EquivalentMergePreservesConflictingPoliciesUntilExplicitReview(string initial)
    {
        var id = Add("merge.jpg"); Start(); var lease = _repo.Claim()!;
        var forest = _repo.Taxonomy().Labels.Single(l => l.Id == "scene.forest");
        var draft = new ClassificationProposalDraft("另一森林标签", forest.Dimension, forest.Definition, forest.Exclusions, ["nav.nature"], ["image:current"]);
        _repo.Publish(lease, Result(lease) with { Proposals = [draft] });
        var proposal = Assert.Single(_repo.Proposals());
        var taxonomy = _repo.Review(new(Request(), proposal.Id, proposal.Revision, _repo.Taxonomy().Revision, "accept"));
        var alias = "dynamic." + proposal.Id;
        void Decide(string label, string action)
        {
            var photo = Assert.Single(_repo.Browse(new(new([], []))).Photos);
            _repo.Decide(new(Request(), _repo.Epoch, label, action,
                new Dictionary<long, string> { [id] = photo.ContentIdentity }, new Dictionary<long, string> { [id] = photo.OwnerId },
                _repo.Taxonomy().Revision, new Dictionary<long, string> { [id] = photo.ManualFingerprint! }));
        }
        Decide(alias, initial); Decide(forest.Id, "reject");
        var merge = new ClassificationTaxonomyEdit(Request(), taxonomy.Revision, alias, MergeInto: forest.Id, ConfirmEquivalent: true);
        var merged = _repo.EditTaxonomy(merge);
        Assert.Equal(merged.Revision, _repo.EditTaxonomy(merge).Revision);
        Assert.Equal(forest.Id, new ClassificationTaxonomy(merged.Labels, merged.Edges).CanonicalId(alias));
        var conflicted = Assert.Single(_repo.Browse(new(new([], []))).Photos);
        Assert.Contains(forest.Id, conflicted.ConflictLabelIds!); Assert.DoesNotContain(forest.Id, conflicted.LabelIds);
        Decide(alias, "confirm");
        var resolved = Assert.Single(_repo.Browse(new(new([new(ClassificationMatchMode.All, [alias])], []))).Photos);
        Assert.Empty(resolved.ConflictLabelIds!); Assert.Contains(forest.Id, resolved.LabelIds);
        Decide(forest.Id, "reject");
        Assert.Empty(Assert.Single(_repo.Browse(new(new([], []))).Photos).ConfirmedIds!);
        Assert.Throws<InvalidOperationException>(() => _repo.EditTaxonomy(new(Request(), merged.Revision, alias, Active: true)));
    }

    [Fact]
    public void MergeRejectsCyclesAndMissingEquivalenceConfirmation()
    {
        var taxonomy = _repo.Taxonomy();
        Assert.Throws<ArgumentException>(() => _repo.EditTaxonomy(new(Request(), taxonomy.Revision, "scene.forest", MergeInto: "scene.grassland")));
        Assert.Throws<ArgumentException>(() => _repo.EditTaxonomy(new(Request(), taxonomy.Revision, "scene.forest", MergeInto: "scene.forest.bamboo", ConfirmEquivalent: true)));
        Assert.Equal(taxonomy.Revision, _repo.Taxonomy().Revision);
    }

    private sealed class CountingModel : IClassificationModel
    {
        public int Calls { get; private set; }
        public Task<ClassificationResult> ClassifyAsync(ClassificationContext context, ClassificationTaxonomySnapshot taxonomy, ClassificationConfiguration configuration, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("model_should_not_run"); }
    }

    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
