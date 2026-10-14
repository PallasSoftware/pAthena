namespace Athena.Net.MapServer.Net;

/// <summary>
/// Writing to the game client failed because its connection is gone (reset, aborted, broken pipe, ...).
/// <para>
/// Thrown only by <c>MapClientSession.WriteAsync</c>, i.e. only for failures of the CLIENT's socket, which is what makes
/// it a reliable classification: an <see cref="IOException"/> from anywhere else (the CharServer link, persistence, the World)
/// stays what it was. A vanished client is an ordinary end of a session, not a defect; the session's background loops end
/// it quietly instead of treating the failed write as an unexpected exception.
/// </para>
/// It derives from <see cref="IOException"/> on purpose: every existing caller that treats an <see cref="IOException"/> from a write
/// as "client disconnected" (<c>MapTcpServer</c>, the fan-out paths) keeps working unchanged.
/// </summary>
public sealed class ClientDisconnectedException : IOException
{
    public ClientDisconnectedException(Exception inner)
        : base($"The client connection was lost while writing to it: {inner.Message}", inner)
    {
    }
}
