using Picer.Core.Classification;

namespace Picer.Tests;

public class ClassificationV2Tests
{
    [Theory]
    [InlineData("Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M", true)]
    [InlineData("fixture-model", false)]
    public void RequiresNoThinkModeOnlyForQwen3Models(string model, bool expected)
        => Assert.Equal(expected, ClassificationModel.RequiresNoThinkMode(model));

    [Fact]
    public async Task Qwen3NoThinkMarkerIsLastUserContentPart()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var context = new ClassificationContext(new(), Input(), new Dictionary<string, string> { ["image:current"] = "fixture" });
        var model = new ClassificationModel((_, _) => Task.FromResult(new byte[] { 255, 216, 255, 217 }), (_, bytes, _) =>
        {
            using var payload = System.Text.Json.JsonDocument.Parse(bytes);
            var format = payload.RootElement.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var schema = format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
            var allowed = schema.GetProperty("labels").GetProperty("items").GetProperty("properties").GetProperty("labelId").GetProperty("enum");
            Assert.Equal(taxonomy.Labels.Where(label => label.Active && label.Assignable).Select(label => label.Id),
                allowed.EnumerateArray().Select(id => id.GetString()));
            Assert.Equal(2048, payload.RootElement.GetProperty("max_tokens").GetInt32());
            var parts = payload.RootElement.GetProperty("messages")[1].GetProperty("content");
            using var input = System.Text.Json.JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
            Assert.Equal(new[] { "labels", "navigation" }, input.RootElement.EnumerateObject().Take(2).Select(field => field.Name));
            Assert.Equal(taxonomy.Labels.Where(label => label.Active && label.Assignable).Select(label => label.Id),
                input.RootElement.GetProperty("labels").EnumerateArray().Select(label => label[0].GetString()));
            var finalPart = parts[parts.GetArrayLength() - 1];
            Assert.Equal("text", finalPart.GetProperty("type").GetString());
            Assert.Equal("/no_think", finalPart.GetProperty("text").GetString());
            var result = new ClassificationResult("classification-result-v1", ClassificationInputFingerprint.Compute(context.Input),
                taxonomy.Labels.Where(label => label.Active && label.Assignable).Select(label => label.Id == "scene.outdoor" ? new ClassificationLabelEvaluation(label.Id, "supported", ["image:current"]) : new ClassificationLabelEvaluation(label.Id, "absent", [])).ToArray(), []);
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { content = System.Text.Json.JsonSerializer.Serialize(result) } } } }));
        });
        await model.ClassifyAsync(context, taxonomy, new(true, "Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M", "local", false), default);
    }

    [Theory]
    [InlineData("paper_text_only", false)]
    [InlineData("other_or_mixed", true)]
    public void PaperTextDoesNotTurnDepictedSubjectsIntoSceneEvidence(string surface, bool sceneSupported)
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var labels = new[] { "document.general", "document.screen_photo", "subject.people", "scene.indoor" };
        var raw = System.Text.Json.JsonSerializer.Serialize(new { surface, schemaVersion = "classification-sparse-v1",
            inputFingerprint = "sample", evaluatedAll = true, proposals = Array.Empty<object>(),
            labels = labels.Select(id => new { labelId = id, support = "supported", evidenceIds = new[] { "image:current" } }) });
        var result = ClassificationModel.DecodeResult(raw, taxonomy);
        Assert.Equal("supported", result.Labels.Single(label => label.LabelId == "document.general").Support);
        foreach (var id in labels.Skip(1))
            Assert.Equal(sceneSupported ? "supported" : "absent", result.Labels.Single(label => label.LabelId == id).Support);
    }

    [Fact]
    public void PaperVerdictsMustCoverEveryEligibleIdExactlyOnce()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var verdicts = preset.Labels.Where(label => label.Active && label.Assignable &&
                ((label.Dimension == "document" && label.Id is not ("document.screenshot" or "document.old_photo" or "document.screen_photo"))
                || label.Id is "object.book" or "object.art"))
            .ToDictionary(label => label.Id, label => label.Id == "document.general");
        string Encode() => System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-paper-v1",
            surface = "paper_text_only", inputFingerprint = "sample", evaluatedAll = true, paperLabels = verdicts, proposals = Array.Empty<object>() });
        var result = ClassificationModel.DecodeResult(Encode(), taxonomy);
        Assert.Equal("document.general", Assert.Single(result.Labels.Where(label => label.Support == "supported")).LabelId);
        var duplicate = Encode().Replace("\"document.general\":true", "\"document.general\":true,\"document.general\":false");
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(duplicate, taxonomy));
        verdicts.Remove("document.receipt");
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Encode(), taxonomy));
        verdicts["scene.indoor"] = false;
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Encode(), taxonomy));
    }

    [Fact]
    public void ReviewChecksDescriptionCandidatesWithoutAutomaticallyAssigningThem()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var context = new ClassificationContext(new(), Input(), new Dictionary<string, string>
        {
            ["description:fixture"] = """{"Scenes":["操场"],"Objects":["多人合影"],"LocationDisplayName":"图书馆"}""",
            ["image:current"] = "fixture"
        });
        var candidates = ClassificationModel.DescriptionCandidates(context, taxonomy);
        Assert.Contains(candidates, label => label.Id == "subject.people");
        Assert.Contains(candidates, label => label.Id == "place.playground");
        Assert.DoesNotContain(candidates, label => label.Id == "place.library");
        var judgments = candidates.ToDictionary(label => label.Id, _ => false);
        string Encode(params string[] extras) => System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "classification-reviewed-v2",
            inputFingerprint = "sample",
            evaluatedAll = true,
            checks = judgments,
            additionalSubjects = extras,
            proposals = Array.Empty<object>()
        });
        var rejected = ClassificationModel.DecodeReview(Encode(), taxonomy, candidates);
        Assert.All(rejected.Labels, label => Assert.Equal("absent", label.Support));
        judgments["place.playground"] = true;
        var accepted = ClassificationModel.DecodeReview(Encode("户外"), taxonomy, candidates);
        Assert.Equal(new[] { "place.playground", "scene.outdoor" }, accepted.Labels.Where(label => label.Support == "supported").Select(label => label.LabelId).Order().ToArray());
        judgments["place.playground"] = false;
        var checkedNegative = ClassificationModel.DecodeReview(Encode("操场", "不存在的名称"), taxonomy, candidates);
        Assert.All(checkedNegative.Labels, label => Assert.Equal("absent", label.Support));
        var repeated = ClassificationModel.DecodeReview(Encode("户外", "户外"), taxonomy, candidates);
        Assert.Single(repeated.Labels.Where(label => label.Support == "supported" && label.LabelId == "scene.outdoor"));
        var copiedVocabulary = taxonomy.Labels.Where(label => label.Active && label.Assignable && !judgments.ContainsKey(label.Id))
            .Select(label => label.Id).Take(13).ToArray();
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeReview(Encode(copiedVocabulary), taxonomy, candidates));
        judgments.Remove("place.playground");
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeReview(Encode(), taxonomy, candidates));
    }

    [Fact]
    public void EmptyReviewProposesVisibleUnmappedSubjectAndDistinguishesPhysicalDisplay()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var candidates = taxonomy.Labels.Where(label => label.Id is "document.screen_photo" or "document.screenshot").ToArray();
        ClassificationResult Review(string name, bool display)
        {
            var content = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-reviewed-v2",
                inputFingerprint = "fixture", evaluatedAll = true, additionalSubjects = Array.Empty<string>(), proposals = Array.Empty<object>(),
                checks = candidates.ToDictionary(label => label.Id, _ => display),
                primarySubject = new { kind = name.Length == 0 ? "none" : "visible_subject", name, visible = "可见方形矩阵和定位图案", boundary = "普通装饰格不计", parentId = "nav.document" } });
            return ClassificationModel.DecodeReview(content, taxonomy, candidates, emptyReview: true);
        }
        var novel = Review("二维码", false);
        Assert.Equal("二维码", Assert.Single(novel.Proposals).Name);
        Assert.DoesNotContain(novel.Labels, label => label.Support == "supported");
        Assert.Empty(Review("", false).Proposals);
        var known = Review("户外", false);
        Assert.Empty(known.Proposals);
        Assert.Contains(known.Labels, label => label.LabelId == "scene.outdoor" && label.Support == "supported");
        var display = Review("屏幕翻拍", true);
        Assert.Empty(display.Proposals);
        Assert.Equal("document.screen_photo", Assert.Single(display.Labels.Where(label => label.Support == "supported")).LabelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrMultiSeasonFirstPassWithDescribedSubjectsRequiresIndependentVisualReview(bool multiSeason)
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var context = new ClassificationContext(new(), Input(), new Dictionary<string, string>
        {
            ["description:fixture"] = """{"Scenes":["操场"]}""",
            ["image:current"] = "fixture"
        });
        var fingerprint = ClassificationInputFingerprint.Compute(context.Input);
        var checks = ClassificationModel.DescriptionCandidates(context, taxonomy).ToDictionary(label => label.Id, _ => false);
        var firstLabels = (multiSeason ? new[] { "environment.season.spring", "environment.season.summer", "environment.season.autumn" } : Array.Empty<string>())
            .Select(id => new { labelId = id, support = "supported", evidenceIds = new[] { "image:current" } });
        var attempts = 0;
        var model = new ClassificationModel((_, _) => Task.FromResult(new byte[] { 255, 216, 255, 217 }), (_, _, _) =>
        {
            var content = ++attempts == 1
                ? System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-sparse-v1", inputFingerprint = fingerprint, evaluatedAll = true, labels = firstLabels, proposals = Array.Empty<object>() })
                : System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-reviewed-v2", inputFingerprint = fingerprint, evaluatedAll = true, checks, additionalSubjects = Array.Empty<string>(), proposals = Array.Empty<object>() });
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
        });
        var config = new ClassificationConfiguration(true, "Qwen3", "local", false);
        var first = await Assert.ThrowsAsync<InvalidDataException>(() => model.ClassifyAsync(context, taxonomy, config, default));
        Assert.Equal("classification_output_incomplete", first.Message);
        var second = await model.ClassifyAsync(context with { RetryIncompleteOutput = true }, taxonomy, config, default);
        Assert.DoesNotContain(second.Labels, label => label.Support == "supported");
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void SparseModelOutputRequiresCompleteEvaluationAndKnownUniqueLabels()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        string Json(bool evaluated, params string[] ids) => System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "classification-sparse-v1", inputFingerprint = "fingerprint", evaluatedAll = evaluated,
            labels = ids.Select(id => new { labelId = id, support = "supported", evidenceIds = new[] { "image:current" } }), proposals = Array.Empty<object>()
        });
        var result = ClassificationModel.DecodeResult(Json(true, "subject.people", "scene.outdoor"), taxonomy);
        var duplicate = Json(true, "scene.outdoor", "scene.outdoor");
        var repeated = ClassificationModel.DecodeResult(duplicate, taxonomy, retry: true);
        Assert.Single(repeated.Labels.Where(label => label.Support == "supported"));
        var contradictory = System.Text.Json.Nodes.JsonNode.Parse(duplicate)!;
        contradictory["labels"]![1]!["support"] = "conflicting";
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(contradictory.ToJsonString(), taxonomy, retry: true));
        contradictory["labels"]![1]!["support"] = "supported";
        contradictory["labels"]![1]!["evidenceIds"]![0] = "description:other";
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(contradictory.ToJsonString(), taxonomy, retry: true));
        var wrapped = ClassificationModel.DecodeResult("<think>checking {untrusted reasoning}</think>\nResult:\n" + Json(true, "subject.people", "scene.outdoor") + "\nDone.", taxonomy);
        Assert.Equal(preset.Labels.Count(label => label.Active && label.Assignable), result.Labels.Count);
        Assert.Equal(result.Labels.Select(label => (label.LabelId, label.Support)),
            wrapped.Labels.Select(label => (label.LabelId, label.Support)));
        Assert.Equal(2, result.Labels.Count(l => l.Support == "supported"));
        Assert.Equal(preset.Labels.Count(label => label.Active && label.Assignable) - 2, result.Labels.Count(l => l.Support == "absent"));
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Json(false), taxonomy));
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Json(true, "unknown"), taxonomy));
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Json(true, "subject.people", "subject.people"), taxonomy));
    }

    [Fact]
    public void IndexedSparseOutputMapsCompactIndicesToValidatedLabelIds()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var expected = preset.Labels.Where(label => label.Active && label.Assignable).ToArray();
        string Json(int[] supported, int[] uncertain, int[] conflicting, bool evaluated = true)
            => System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = "classification-indexed-v1", inputFingerprint = "fingerprint", evaluatedAll = evaluated,
                supported, uncertain, conflicting, proposals = Array.Empty<object>()
            });
        var result = ClassificationModel.DecodeResult(Json([0], [1], [2]), taxonomy);
        Assert.Equal(expected[0].Id, result.Labels[0].LabelId);
        Assert.Equal("supported", result.Labels[0].Support);
        Assert.Equal(new[] { "image:current" }, result.Labels[0].EvidenceIds);
        Assert.Equal("uncertain", result.Labels[1].Support);
        Assert.Equal("conflicting", result.Labels[2].Support);
        Assert.All(result.Labels.Skip(3), label => Assert.Equal("absent", label.Support));
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Json([0], [0], []), taxonomy));
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Json([expected.Length], [], []), taxonomy));
        Assert.Throws<InvalidDataException>(() => ClassificationModel.DecodeResult(Json([], [], [], evaluated: false), taxonomy));
    }

    [Fact]
    public void DeletedPersonFilterMatchesNothingInsteadOfBroadeningResults()
    {
        var filter = new CompiledClassificationFilter(Graph(), new([new(ClassificationMatchMode.All, ["person.confirmed.123"])], []));
        Assert.False(filter.Matches(["subject.people"]));
    }
    [Fact]
    public async Task ModelReceivesLiteralChineseTaxonomyWithoutNestedUnicodeEscapes()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges,
            preset.Labels.Where(l => l.Assignable).Select(l => new ClassificationDefinitionVersion(l.Id, 1)).ToArray());
        var context = new ClassificationContext(new(), Input(), new Dictionary<string, string>
        {
            ["image:current"] = "本次核验的照片",
            ["description:fixture"] = System.Text.Json.JsonSerializer.Serialize(new { summary = "户外花园与人物" })
        });
        var model = new ClassificationModel((_, _) => Task.FromResult(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }), (_, bytes, _) =>
        {
            using var payload = System.Text.Json.JsonDocument.Parse(bytes);
            var text = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("人物", text);
            Assert.DoesNotContain("\\u", text);
            using var decoded = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal(preset.Labels.Count(label => label.Active && label.Assignable), decoded.RootElement.GetProperty("labels").GetArrayLength());
            Assert.Equal("户外花园与人物", decoded.RootElement.GetProperty("untrustedEvidence").GetProperty("description:fixture").GetProperty("summary").GetString());
            var result = new ClassificationResult("classification-result-v1", ClassificationInputFingerprint.Compute(context.Input),
                taxonomy.Labels.Where(l => l.Assignable && l.Active).Select(l => new ClassificationLabelEvaluation(l.Id, l.Id == "scene.outdoor" ? "supported" : "absent", l.Id == "scene.outdoor" ? ["image:current"] : [])).ToArray(), []);
            var response = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = System.Text.Json.JsonSerializer.Serialize(result) } } } });
            return Task.FromResult(response);
        });
        Assert.Equal(preset.Labels.Count(label => label.Active && label.Assignable), (await model.ClassifyAsync(context, taxonomy, new(true, "fixture", "local", false), default)).Labels.Count);
    }
    private static ClassificationInput Input() => new("epoch", "owner", "sha256:" + new string('a', 64),
        [], [], [], "model", "prep", "prompt", [new("subject.people", 1)], 1, "candidate");
    private static ClassificationEvidence Evidence(string id = "description", char hash = 'b') => new(id, new string(hash, 64), "source");
    private static ClassificationTaxonomy Graph() => ClassificationPreset.Load().CreateTaxonomy();

    [Fact]
    public async Task StreamEnvelopeOverOneMegabyteDoesNotRejectSmallValidContent()
    {
        var preset = ClassificationPreset.Load();
        var taxonomy = new ClassificationTaxonomySnapshot(1, 1, preset.Labels, preset.Edges, []);
        var context = new ClassificationContext(new(), Input(), new Dictionary<string, string> { ["image:current"] = "fixture" });
        var content = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "classification-sparse-v1", inputFingerprint = ClassificationInputFingerprint.Compute(context.Input),
            evaluatedAll = true, labels = Array.Empty<object>(), proposals = Array.Empty<object>()
        });
        var envelope = "data: " + System.Text.Json.JsonSerializer.Serialize(new
        { padding = new string('x', 1100000), choices = new[] { new { delta = new { content } } } }) + "\n\ndata: [DONE]\n\n";
        var model = new ClassificationModel((_, _) => Task.FromResult(new byte[] { 255, 216, 255, 217 }),
            (_, _, _) => Task.FromResult(envelope));
        Assert.Equal(preset.Labels.Count(label => label.Active && label.Assignable), (await model.ClassifyAsync(context, taxonomy, new(true, "fixture", "local", false), default)).Labels.Count);
    }

    [Fact]
    public void BatchFingerprintPreservesCanonicalFormatAndDoesNotReuseChangedDefinitions()
    {
        var definitions = new[] { new ClassificationDefinitionVersion("subject.people", 1), new("scene.forest", 2) };
        var input = Input() with { Definitions = definitions, Descriptions = [Evidence()] };
        var compute = ClassificationInputFingerprint.CreateBatchComputer(definitions);
        Assert.Equal(ClassificationInputFingerprint.Compute(input), compute(input));
        var reordered = input with { Definitions = definitions.Reverse().ToArray() };
        Assert.Equal(ClassificationInputFingerprint.Compute(input), compute(reordered));
        definitions[0] = new("subject.people", 3);
        Assert.Equal(ClassificationInputFingerprint.Compute(input), compute(input));
        var invalid = input with { Definitions = [new("subject.people", 0)] };
        Assert.Throws<ArgumentException>(() => compute(invalid));
    }

    [Fact]
    public void ExplicitModelAgentIsEncodedAndSpaceRouteIsPreserved()
    {
        var endpoint = ClassificationModel.ServiceRelayEndpoint(new Uri("https://example.test/spaces/space-id/api/service/v1/classification/"), "agent/a+b");
        Assert.Equal("/spaces/space-id/api/ai/v1/chat/stream", endpoint.AbsolutePath);
        Assert.Equal("?agentId=agent%2Fa%2Bb", endpoint.Query);
    }

    [Fact]
    public void PresetHasDefinedAssignableLabelsAndAmbiguousLegacyAliases()
    {
        var graph = Graph();
        Assert.Equal(ClassificationPreset.Load().Labels.Count, graph.Labels.Count);
        Assert.Equal(ClassificationPreset.Load().Labels.Count(x => x.Assignable), graph.Labels.Values.Count(x => x.Assignable));
        Assert.All(graph.Labels.Values.Where(x => x.Assignable), x => Assert.False(string.IsNullOrWhiteSpace(x.Exclusions)));
        Assert.Equal(new[] { "subject.group", "subject.group_portrait" }, graph.ResolveAlias("合影"));
        Assert.Equal(new[] { "subject.people", "subject.portrait" }, graph.ResolveAlias("人像"));
    }

    [Fact]
    public void MultiParentCampingIsOneIdAndRelatedForestDoesNotMatchIt()
    {
        var graph = Graph();
        Assert.Contains("activity.camping", graph.Expand("nav.event"));
        Assert.Contains("activity.camping", graph.Expand("nav.activity"));
        Assert.DoesNotContain("activity.camping", graph.Expand("scene.forest"));
        Assert.Equal(1, graph.Expand("nav.activity").Count(x => x == "activity.camping"));
    }

    [Fact]
    public void ExcessiveGraphDepthIsRejected()
    {
        var labels = Enumerable.Range(0, 17).Select(i => new ClassificationLabel("node" + i, "subject", "节点", "定义", "边界", [])).ToArray();
        var edges = Enumerable.Range(0, 16).Select(i => new ClassificationEdge("node" + i, "node" + (i + 1), TaxonomyEdgeKind.Browse));
        Assert.Throws<ArgumentException>(() => new ClassificationTaxonomy(labels, edges));
    }

    [Theory]
    [InlineData(TaxonomyEdgeKind.IsA)]
    [InlineData(TaxonomyEdgeKind.Browse)]
    public void MixedNavigationCyclesIncludingInactiveNodesAreRejected(TaxonomyEdgeKind kind)
    {
        ClassificationLabel[] labels = [new("a", "subject", "甲", "定义", "边界", []), new("b", "subject", "乙", "定义", "边界", [], Active: false)];
        Assert.Throws<ArgumentException>(() => new ClassificationTaxonomy(labels, [new("a", "b", kind), new("b", "a", TaxonomyEdgeKind.Browse)]));
    }

    [Fact]
    public void GroupedFilterSupportsPeopleAndForestOrGrassWithGlobalExclusion()
    {
        var filter = new CompiledClassificationFilter(Graph(), new([
            new(ClassificationMatchMode.All, ["subject.people"]),
            new(ClassificationMatchMode.Any, ["scene.forest", "scene.grassland"])], ["scene.forest.bamboo"]));
        Assert.True(filter.Matches(["subject.people", "scene.grassland"]));
        Assert.True(filter.Matches(["subject.people", "scene.forest.conifer"]));
        Assert.False(filter.Matches(["scene.grassland"]));
        Assert.False(filter.Matches(["subject.people", "activity.camping"]));
        Assert.False(filter.Matches(["subject.people", "scene.grassland", "scene.forest.bamboo"]));
    }

    [Fact]
    public void LegacyConceptsDoNotGainFacilitiesOrLoseBroadBeachMeaning()
    {
        var graph = Graph();
        Assert.DoesNotContain("object.building.road", graph.Expand("object.building"));
        Assert.Contains("object.building.road", graph.Expand("nav.architecture"));
        Assert.DoesNotContain("scene.beach", graph.Expand("scene.coast.sandy"));
        Assert.Equal(new[] { "scene.beach", "scene.coast.sandy" }, graph.ResolveAlias("沙滩"));
        Assert.Contains("海边", graph.Labels["scene.beach"].Definition);
        Assert.Throws<ArgumentException>(() => new ClassificationTaxonomy([new("a", "typo", "甲", "定义", "边界", [])], []));
    }

    [Fact]
    public void EmptyFiltersMatchAllButUnknownIdsAndOversizedGroupsFail()
    {
        Assert.True(new CompiledClassificationFilter(Graph(), new([new(ClassificationMatchMode.Any, [])], [])).Matches([]));
        Assert.Throws<ArgumentException>(() => new CompiledClassificationFilter(Graph(), new([], ["missing"])));
        Assert.Throws<ArgumentException>(() => new CompiledClassificationFilter(Graph(), new([new(ClassificationMatchMode.All, Enumerable.Repeat("subject.people", 33).ToArray())], [])));
    }

    [Theory]
    [InlineData(ClassificationMatchMode.All, false)]
    [InlineData(ClassificationMatchMode.Any, true)]
    public void ExplicitParentAndChildConditionsRemainIndependent(ClassificationMatchMode mode, bool parentOnlyMatches)
    {
        var filter = new CompiledClassificationFilter(Graph(), new([new(mode, ["scene.forest", "scene.forest.bamboo"])], []));
        Assert.Equal(parentOnlyMatches, filter.Matches(["scene.forest"]));
        Assert.True(filter.Matches(["scene.forest.bamboo"]));
        Assert.False(new CompiledClassificationFilter(Graph(), new([], ["scene.forest"])).Matches(["scene.forest.bamboo"]));
    }

    [Fact]
    public void FingerprintMatchesIndependentPythonGoldenVector()
        => Assert.Equal("cf90bc918e2830eb75bb2ed1917646cf5113a326fe784c9ab7088af66ce8ea03", ClassificationInputFingerprint.Compute(Input()));

    [Fact]
    public void EvidenceOrderDuplicatesAndHashCaseDoNotChangeFingerprint()
    {
        var first = Input() with { Descriptions = [Evidence("a"), Evidence("z")] };
        var reordered = first with { Descriptions = [Evidence("z", 'B'), Evidence("a"), Evidence("a")] };
        Assert.Equal(ClassificationInputFingerprint.Compute(first), ClassificationInputFingerprint.Compute(reordered));
        Assert.Equal(ClassificationPlanAction.Skip, ClassificationIncrementalPlanner.Decide(reordered, first).Action);
    }

    [Fact]
    public void ConflictingEvidenceAndUnverifiedMediaAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ClassificationInputFingerprint.Compute(Input() with { Descriptions = [Evidence(), Evidence(hash: 'c')] }));
        Assert.Throws<ArgumentException>(() => ClassificationInputFingerprint.Compute(Input() with { MediaIdentity = "C:/photos/a.jpg" }));
        Assert.Throws<ArgumentException>(() => ClassificationInputFingerprint.Compute(Input() with { Definitions = [new("a", 1), new("a", 2)] }));
    }

    [Theory]
    [InlineData("description", ClassificationUpdateReason.DescriptionChanged)]
    [InlineData("people", ClassificationUpdateReason.PeopleChanged)]
    [InlineData("metadata", ClassificationUpdateReason.MetadataChanged)]
    [InlineData("model", ClassificationUpdateReason.ModelChanged)]
    [InlineData("prompt", ClassificationUpdateReason.PromptChanged)]
    [InlineData("definition", ClassificationUpdateReason.TaxonomyChanged)]
    [InlineData("discovery", ClassificationUpdateReason.TaxonomyChanged)]
    [InlineData("candidate", ClassificationUpdateReason.CandidatePolicyChanged)]
    [InlineData("owner", ClassificationUpdateReason.MediaChanged)]
    [InlineData("epoch", ClassificationUpdateReason.MediaChanged)]
    public void ChangedInputsProduceSpecificIncrementalReason(string field, ClassificationUpdateReason reason)
    {
        var previous = Input();
        var current = field switch
        {
            "description" => previous with { Descriptions = [Evidence()] },
            "people" => previous with { People = [Evidence()] },
            "metadata" => previous with { Metadata = [Evidence()] },
            "model" => previous with { ModelVersion = "model-v2" },
            "prompt" => previous with { PromptVersion = "prompt-v2" },
            "definition" => previous with { Definitions = [new("subject.people", 2)] },
            "discovery" => previous with { DiscoveryRevision = 2 },
            "candidate" => previous with { CandidatePolicyVersion = "candidate-v2" },
            "owner" => previous with { OwnerId = "another-owner" },
            _ => previous with { SpaceEpoch = "epoch-v2" }
        };
        var decision = ClassificationIncrementalPlanner.Decide(current, previous);
        Assert.Equal(ClassificationPlanAction.Enqueue, decision.Action);
        Assert.Equal(reason, decision.Reasons);
    }

    [Fact]
    public void FailedAttemptNeverBecomesSuccessAndObsoleteFailureDoesNotBlockNewEvidence()
    {
        var input = Input();
        var now = DateTimeOffset.UtcNow;
        var failed = new ClassificationAttempt(ClassificationInputFingerprint.Compute(input), now.AddMinutes(1), true);
        Assert.Equal(ClassificationPlanAction.DeferRetry, ClassificationIncrementalPlanner.Decide(input, null, failed, now: now).Action);
        var retry = ClassificationIncrementalPlanner.Decide(input, null, failed, now: now.AddMinutes(2));
        Assert.Equal(ClassificationUpdateReason.NewMedia | ClassificationUpdateReason.RetryDue, retry.Reasons);
        Assert.Equal(ClassificationPlanAction.Enqueue, retry.Action);
        Assert.Equal(ClassificationPlanAction.Enqueue, ClassificationIncrementalPlanner.Decide(input with { Descriptions = [Evidence()] }, input, failed, now: now).Action);
    }

    [Fact]
    public void RunningLeaseDeduplicatesSameInputAndQueuesChangedSuccessor()
    {
        var input = Input();
        var lease = ClassificationInputFingerprint.Compute(input);
        Assert.Equal(ClassificationPlanAction.AlreadyLeased, ClassificationIncrementalPlanner.Decide(input, null, leasedFingerprint: lease).Action);
        Assert.Equal(ClassificationPlanAction.QueueSuccessor, ClassificationIncrementalPlanner.Decide(input with { Descriptions = [Evidence()] }, input, leasedFingerprint: lease).Action);
        Assert.Equal(ClassificationUpdateReason.Interrupted, ClassificationIncrementalPlanner.Decide(input, input, interrupted: true).Reasons);
    }

    [Fact]
    public void EqualDimensionDoesNotPermitMixingEmbeddingSpaces()
    {
        var space = new ClassificationEmbeddingSpace(new string('a', 64), "tokenizer", "prep", 512, "l2-v1");
        Assert.True(space.IsCompatibleWith(space with { ArtifactSha256 = new string('A', 64) }));
        Assert.False(space.IsCompatibleWith(space with { ArtifactSha256 = new string('b', 64) }));
        Assert.False(space.IsCompatibleWith(space with { TokenizerVersion = "" }));
        Assert.False(space.IsCompatibleWith(space with { PreprocessingVersion = "prep-v2" }));
        Assert.False(space.IsCompatibleWith(space with { NormalizationVersion = "l2-v2" }));
    }
}
