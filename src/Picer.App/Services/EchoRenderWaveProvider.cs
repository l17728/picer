using NAudio.Wave;

namespace Picer.App.Services;

internal sealed class EchoRenderWaveProvider(IWaveProvider source, Action<byte[], int, int> renderSink) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0) renderSink(buffer, offset, read);
        return read;
    }
}
