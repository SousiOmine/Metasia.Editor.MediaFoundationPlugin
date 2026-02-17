using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace MediaFoundationPlugin;

internal static class MediaFoundationLifecycle
{
    private static readonly object Sync = new();
    private static int _refCount;

    public static void EnsureStarted()
    {
        lock (Sync)
        {
            if (_refCount == 0)
            {
                MediaFactory.MFStartup().CheckError();
            }

            _refCount++;
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (_refCount <= 0)
            {
                return;
            }

            _refCount--;
            if (_refCount == 0)
            {
                Result result = MediaFactory.MFShutdown();
                if (result.Failure)
                {
                    Debug.WriteLine($"MediaFoundationPlugin: MFShutdown failed with {result.Code}");
                }
            }
        }
    }
}