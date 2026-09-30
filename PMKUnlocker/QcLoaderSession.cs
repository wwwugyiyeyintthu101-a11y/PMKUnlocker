namespace PMKUnlocker;

internal sealed class QcLoaderSession
{
    private string loaderPath = "";
    internal bool Authenticated { get; set; }
    internal string Port { get; set; } = "";
    internal string LoaderPath
    {
        get => loaderPath;
        set
        {
            loaderPath = value ?? "";
            // A loader selection defines a new session, even on the same COM port.
            Authenticated = false;
            Port = "";
        }
    }
}
