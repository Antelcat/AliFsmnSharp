using System.Reflection;
using System.Runtime.InteropServices;

namespace AliFsmnSharp.Interop;

public static class KaldiNativeFbank
{
    private const string LibFbank = "kaldi-native-fbank-dll";

    static KaldiNativeFbank()
    {
        NativeLibrary.SetDllImportResolver(typeof(KaldiNativeFbank).Assembly, (s, a, p) =>
        {
            return NativeLibrary.Load(NativeLibraryPath ??
                                      Path.Combine(AppContext.BaseDirectory, "runtimes",
                                          string.Format("{0}-{2}/native/{1}",
                                              (string[])
                                              [
                                                  ..(string[])(Environment.OSVersion.Platform switch
                                                  {
                                                      PlatformID.Win32Windows or
                                                          PlatformID.Win32NT or
                                                          PlatformID.WinCE or
                                                          PlatformID.Win32NT => ["win", "kaldi-native-fbank-dll.dll"],
                                                      PlatformID.Unix   => ["linux", "libkaldi-native-fbank-core.so"],
                                                      PlatformID.MacOSX => ["osx", "libkaldi-native-fbank-core.dylib"],
                                                      _                 => throw new PlatformNotSupportedException()
                                                  }),
                                                  RuntimeInformation.ProcessArchitecture switch
                                                  {
                                                      Architecture.X64   => "x64",
                                                      Architecture.X86   => "x86",
                                                      Architecture.Arm   => "arm",
                                                      Architecture.Arm64 => "arm64",
                                                      _                  => throw new PlatformNotSupportedException()
                                                  }
                                              ])));
        });
    }

    public static string? NativeLibraryPath { get; set; }

    [DllImport(LibFbank, EntryPoint = nameof(GetFbankOptions), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr GetFbankOptions(float dither, bool snip_edges, float sample_rate, int num_bins,
        float frame_shift = 10.0f, float frame_length = 25.0f, float energy_floor = 0.0f, bool debug_mel = false,
        string window_type = "hamming");

    [DllImport(LibFbank, EntryPoint = nameof(GetOnlineFbank), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern KnfOnlineFbank GetOnlineFbank(IntPtr opts);

    [DllImport(LibFbank, EntryPoint = nameof(AcceptWaveform), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AcceptWaveform(KnfOnlineFbank knfOnlineFbank, float sample_rate, float[] samples,
        int samples_size);

    [DllImport(LibFbank, EntryPoint = nameof(InputFinished), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void InputFinished(KnfOnlineFbank knfOnlineFbank);

    [DllImport(LibFbank, EntryPoint = nameof(GetNumFramesReady), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetNumFramesReady(KnfOnlineFbank knfOnlineFbank);

    [DllImport(LibFbank, EntryPoint = nameof(AcceptWaveformxxx), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern FbankDatas AcceptWaveformxxx(KnfOnlineFbank knfOnlineFbank, float sample_rate,
        float[] samples, int samples_size);

    [DllImport(LibFbank, EntryPoint = nameof(GetFbank), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GetFbank(KnfOnlineFbank knfOnlineFbank, int frame, ref FbankData pData);

    [DllImport(LibFbank, EntryPoint = nameof(GetFbanks), CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void GetFbanks(KnfOnlineFbank knfOnlineFbank, int framesNum, ref FbankDatas fbankDatas);

}