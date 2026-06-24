namespace VPNTray.Util;

public static class SingleInstance
{
    private const string MutexName = "Global\\VPNTray_SingleInstance";

    /// <summary>Returns true if this is the first instance. Keep `handle` alive for the app lifetime.</summary>
    public static bool TryAcquire(out Mutex? handle)
    {
        handle = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            handle.Dispose();
            handle = null;
        }
        return createdNew;
    }
}
