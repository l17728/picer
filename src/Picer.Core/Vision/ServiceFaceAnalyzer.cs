using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Picer.Core.Models;

namespace Picer.Core.Vision;

/// <summary>
/// Face analysis proxy used by both Local and Remote GUI modes. The GUI never
/// loads an ONNX face model; the request is relayed by IndexService to the
/// source-machine MediaAgent.
/// </summary>
public sealed class ServiceFaceAnalyzer : IImageFaceAnalyzer, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly string _modelVersion;

    public ServiceFaceAnalyzer(Uri serviceEndpoint, HttpClient authorizedClient, string modelVersion = "service-face")
    {
        _endpoint = new Uri(serviceEndpoint, "api/service/v1/face/detect").ToString();
        _modelVersion = modelVersion;
        _client = authorizedClient;
    }

    public ServiceFaceAnalyzer(Uri serviceEndpoint, string bearerToken, byte[]? expectedSpkiPin = null, string modelVersion = "service-face")
    {
        _endpoint = new Uri(serviceEndpoint, "api/service/v1/face/detect").ToString();
        _modelVersion = modelVersion;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                if (expectedSpkiPin is null) return true;
                var actual = new X509Certificate2(certificate).PublicKey.ExportSubjectPublicKeyInfo();
                return CryptographicOperations.FixedTimeEquals(actual, expectedSpkiPin);
            }
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public string AnalyzerName => "index-service-face";
    public string ModelVersion => _modelVersion;

    public async Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
    {
        var payload = new { imageId = image.Id, path = image.Path, modelVersion = _modelVersion };
        using var response = await _client.PostAsJsonAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"IndexService face analysis failed ({(int)response.StatusCode})");
        var result = JsonSerializer.Deserialize<FaceServiceResult>(text)
            ?? throw new InvalidOperationException("IndexService face result is empty");
        if (!FaceServiceContract.IsValid(result, "detect"))
            throw new InvalidOperationException("IndexService face result schema invalid");
        if (!string.Equals(result.ModelVersion, _modelVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Face service returned model '{result.ModelVersion}' but '{_modelVersion}' was requested.");
        return new FaceAnalysisResult(result.Faces.Select(face => new FaceDetection(
            new NormalizedFaceBox(face.X, face.Y, face.Width, face.Height),
            face.Confidence,
            Embedding: face.Embedding?.ToArray())).ToArray()) { ModelVersion = result.ModelVersion };
    }

    public void Dispose() => _client.Dispose();
}
