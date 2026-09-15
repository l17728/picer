using System.IO;
using System.Runtime.InteropServices;

namespace Picer.App.Services;

/// <summary>
/// Loads Picer's stable C ABI bridge for WebRTC AEC3. Native processing is
/// optional so the application remains usable when the runtime pack is absent.
/// </summary>
public sealed unsafe class WebRtcAec3Processor : IDisposable
{
    private const int FrameMilliseconds = 10;
    private const string DllName = "picer_aec3.dll";
    private readonly object _gate = new();
    private readonly nint _library;
    private readonly DestroyDelegate _destroy;
    private readonly ProcessDelegate _processRender;
    private readonly ProcessCaptureDelegate _processCapture;
    private readonly byte[] _pendingRender;
    private nint _handle;
    private int _pendingRenderBytes;

    private WebRtcAec3Processor(nint library, nint handle, int sampleRate,
        DestroyDelegate destroy, ProcessDelegate processRender, ProcessCaptureDelegate processCapture,
        string version)
    {
        _library = library;
        _handle = handle;
        _destroy = destroy;
        _processRender = processRender;
        _processCapture = processCapture;
        SampleRate = sampleRate;
        FrameSamples = sampleRate * FrameMilliseconds / 1000;
        _pendingRender = new byte[FrameSamples * sizeof(short)];
        Version = version;
    }

    public int SampleRate { get; }
    public int FrameSamples { get; }
    public string Version { get; }

    public static bool TryCreate(int sampleRate, out WebRtcAec3Processor? processor, out string detail)
    {
        processor = null;
        var configured = Environment.GetEnvironmentVariable("PICER_AEC3_PATH");
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? new[]
            {
                Path.Combine(AppContext.BaseDirectory, "runtime", "webrtc", DllName),
                Path.Combine(AppContext.BaseDirectory, DllName)
            }
            : new[] { Path.GetFullPath(configured) };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            detail = "AEC3 运行组件未安装";
            return false;
        }

        nint library = 0;
        try
        {
            library = NativeLibrary.Load(path);
            var create = Load<CreateDelegate>(library, "picer_aec3_create");
            var destroy = Load<DestroyDelegate>(library, "picer_aec3_destroy");
            var render = Load<ProcessDelegate>(library, "picer_aec3_process_render");
            var capture = Load<ProcessCaptureDelegate>(library, "picer_aec3_process_capture");
            var getVersion = Load<GetVersionDelegate>(library, "picer_aec3_version");
            var handle = create(sampleRate, sampleRate);
            if (handle == 0) throw new InvalidOperationException("AEC3 初始化失败");
            var versionPointer = getVersion();
            var version = Marshal.PtrToStringUTF8(versionPointer) ?? "webrtc-aec3";
            processor = new WebRtcAec3Processor(library, handle, sampleRate, destroy, render, capture, version);
            detail = version;
            return true;
        }
        catch (Exception exception) when (exception is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            if (library != 0) NativeLibrary.Free(library);
            detail = $"AEC3 不可用：{exception.Message}";
            return false;
        }
    }

    public bool ProcessCapture(Span<byte> pcm, int streamDelayMilliseconds)
    {
        if (pcm.Length % (FrameSamples * sizeof(short)) != 0) return false;
        lock (_gate)
        {
            if (_handle == 0) return false;
            var samples = MemoryMarshal.Cast<byte, short>(pcm);
            for (var offset = 0; offset < samples.Length; offset += FrameSamples)
            {
                fixed (short* pointer = samples.Slice(offset, FrameSamples))
                {
                    if (_processCapture(_handle, pointer, FrameSamples, streamDelayMilliseconds) != 0) return false;
                }
            }
            return true;
        }
    }

    public bool FeedRender(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_handle == 0) return false;
            while (!pcm.IsEmpty)
            {
                var copied = Math.Min(_pendingRender.Length - _pendingRenderBytes, pcm.Length);
                pcm[..copied].CopyTo(_pendingRender.AsSpan(_pendingRenderBytes));
                _pendingRenderBytes += copied;
                pcm = pcm[copied..];
                if (_pendingRenderBytes != _pendingRender.Length) continue;

                var samples = MemoryMarshal.Cast<byte, short>(_pendingRender);
                fixed (short* pointer = samples)
                {
                    if (_processRender(_handle, pointer, FrameSamples) != 0) return false;
                }
                _pendingRenderBytes = 0;
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == 0) return;
            _destroy(_handle);
            _handle = 0;
            NativeLibrary.Free(_library);
        }
    }

    private static T Load<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateDelegate(int captureSampleRate, int renderSampleRate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessDelegate(nint handle, short* samples, int sampleCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessCaptureDelegate(nint handle, short* samples, int sampleCount, int streamDelayMilliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetVersionDelegate();
}
