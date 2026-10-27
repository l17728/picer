using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Picer.Core.Gateway;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FaceOrganizationMutationTests
{
    [Fact]
    public async Task RemoteOrganizationPersistsOnServerAndRejectsStaleMirror()
    {
        using var fixture = new GatewayServiceFixture();
        var images = fixture.Db.GetImages(fixture.SourceId).Take(2).ToArray();
        foreach (var image in images)
            fixture.Db.UpsertMirroredFace(new GatewayDetectedFace(100 + image.Id, image.Id, "organization-model", 1,
                0, 0, 1, 1, 0.99, null, BitConverter.GetBytes(1f), DateTime.UtcNow.ToString("O")));
        using var epochQuery = fixture.Db.Command("SELECT epoch FROM mirror_revision WHERE id=1");
        var epoch = (string)epochQuery.ExecuteScalar()!;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                MediaAgentSettings.MatchesCertificate(certificate, fixture.SpkiPin)
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "picer-test-token");
        using (var stale = await client.PostAsJsonAsync("/api/service/v1/faces/organize", new FaceOrganizationRequest("organization-model", "cluster", "stale")))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Empty(new VisionAnalysisRepository(fixture.Db).GetCandidateClusters("organization-model"));
        using (var invalid = await client.PostAsJsonAsync("/api/service/v1/faces/organize", new FaceOrganizationRequest("organization-model", "delete", epoch)))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var response = await client.PostAsJsonAsync("/api/service/v1/faces/organize", new FaceOrganizationRequest("organization-model", "cluster", epoch));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<FaceOrganizationResult>();
        Assert.Equal(1, result!.Clusters.ClusterCount);
        var group = Assert.Single(new VisionAnalysisRepository(fixture.Db).GetCandidateClusters("organization-model"));
        Assert.Equal(2, group.Faces.Count);
        Assert.Throws<InvalidOperationException>(() => new VisionAnalysisRepository(fixture.Db)
            .ReplaceCandidateClusters("organization-model", [], "stale"));
        using (var personQuery = fixture.Db.Command("SELECT id FROM people LIMIT 1"))
            Assert.Throws<InvalidOperationException>(() => new Picer.Core.People.PeopleService(fixture.Db)
                .SuggestFace(group.Faces[0].FaceId, Convert.ToInt64(personQuery.ExecuteScalar()), 0.99, "stale"));
        Assert.Equal(2, Assert.Single(new VisionAnalysisRepository(fixture.Db).GetCandidateClusters("organization-model")).Faces.Count);
        var removal = new FaceCandidateRemovalRequest(group.Id, group.Faces[0].FaceId, epoch);
        using (var conflict = await client.PostAsJsonAsync("/api/service/v1/faces/candidate/remove", removal with { ClusterId = group.Id + 1000 }))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var removed = await client.PostAsJsonAsync("/api/service/v1/faces/candidate/remove", removal)) removed.EnsureSuccessStatusCode();
        using (var repeated = await client.PostAsJsonAsync("/api/service/v1/faces/candidate/remove", removal)) repeated.EnsureSuccessStatusCode();
        using (var members = fixture.Db.Command("SELECT COUNT(*) FROM face_candidate_members")) Assert.Equal(1L, members.ExecuteScalar());
        using var confirmed = fixture.Db.Command("SELECT COUNT(*) FROM face_person_assignments WHERE state='confirmed'");
        Assert.Equal(1L, confirmed.ExecuteScalar());
        var otherSource = fixture.Db.AddSource(Path.Combine(Path.GetTempPath(), "picer-organization-other-source"));
        using (var ownership = fixture.Db.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                VALUES('another-source','library','device','grant','space','receiving','epoch');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
                VALUES('another-source','image','1',lower(hex(randomblob(16))),$image);
            INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
                VALUES('another-source',1,$source,lower(hex(randomblob(16))),'other-source');
            UPDATE images SET source_id=$source WHERE id=$image;
            """))
        {
            ownership.Parameters.AddWithValue("$image", images[1].Id);
            ownership.Parameters.AddWithValue("$source", otherSource.Id);
            ownership.ExecuteNonQuery();
        }
        using var separated = await client.PostAsJsonAsync("/api/service/v1/faces/organize", new FaceOrganizationRequest("organization-model", "cluster", epoch));
        separated.EnsureSuccessStatusCode();
        Assert.Equal(0, (await separated.Content.ReadFromJsonAsync<FaceOrganizationResult>())!.Clusters.ClusterCount);
        Assert.Equal(1L, confirmed.ExecuteScalar());
        var people = new Picer.Core.People.PeopleService(fixture.Db);
        var known = people.Create("test-known");
        people.ConfirmFace(100 + images[0].Id, known.Id);
        using var suggestions = await client.PostAsJsonAsync("/api/service/v1/faces/organize",
            new FaceOrganizationRequest("organization-model", "suggest", epoch));
        suggestions.EnsureSuccessStatusCode();
        Assert.Equal(0, (await suggestions.Content.ReadFromJsonAsync<FaceOrganizationResult>())!.Suggestions.Suggested);
        var matcher = new FaceSuggestionEngine(new VisionAnalysisRepository(fixture.Db), people)
            .CreateMatcher("organization-model", "publisher:another-source");
        Assert.Null(matcher.Match([1f], 0.99f, 1f).PersonId);
    }
}
