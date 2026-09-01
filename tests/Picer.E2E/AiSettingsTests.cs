using System.IO;
using Picer.App;

namespace Picer.E2E;

public sealed class AiSettingsTests
{
    [Fact]
    public void OpenCodeImport_ReadsProvidersModelsCapabilitiesAndKeys()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-opencode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var path=Path.Combine(root,"opencode.jsonc");
        File.WriteAllText(path,"""
            {
              // JSONC comments are supported
              "provider": {
                "huawei": {
                  "name": "华为",
                  "options": { "baseURL": "https://gateway.example/v1/", "apiKey": "test-secret" },
                  "models": {
                    "language-model": { "name": "Language" },
                    "vision-model": { "name": "Vision", "attachment": true },
                  }
                }
              }
            }
            """);
        try
        {
            var result=OpenCodeModelImporter.Import(path);var provider=Assert.Single(result.Providers);Assert.Equal("huawei",provider.Id);Assert.Equal("https://gateway.example/v1",provider.BaseUrl);
            Assert.Equal(AiModelCapability.Language,provider.Models.Single(x=>x.Id=="language-model").Capability);Assert.Equal(AiModelCapability.Multimodal,provider.Models.Single(x=>x.Id=="vision-model").Capability);Assert.Equal("test-secret",result.ApiKeys["huawei"]);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void LegacyCloudSettings_AreMigratedToPerFeatureBindings()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var path=Path.Combine(root,"cloud-vision.json");
        File.WriteAllText(path,"{\"Enabled\":true,\"Provider\":1}");
        try
        {
            var settings=CloudVisionSettings.Load(path);Assert.Equal("zhipu",settings.Resolve(AiFeatureIds.PhotoAnalysis)?.Provider.Id);Assert.Equal("glm-4.6v-flash",settings.Resolve(AiFeatureIds.VideoSummary)?.Model.Id);
            Assert.DoesNotContain(AiFeatureIds.AiQuestion,settings.Features.Keys);
        }
        finally{Directory.Delete(root,true);}
    }
}
