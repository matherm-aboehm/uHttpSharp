using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using uhttpsharp.Clients;

namespace uhttpsharp.Listeners
{
    public class TcpListenerAdapter : IHttpListener
    {
        private readonly TcpListener _listener;
        private static readonly Action<object> _cancelAcceptCallback = CancelAccept;

        public TcpListenerAdapter(TcpListener listener)
        {
            _listener = listener;
            _listener.Start();
        }

        private static void CancelAccept(object state)
        {
            ((TcpListener)state).Stop();
        }

        public async Task<IClient> GetClient(CancellationToken cancellationToken = default(CancellationToken))
        {
            using (cancellationToken.Register(_cancelAcceptCallback, _listener))
            {
                try
                {
                    return new TcpClientAdapter(await _listener.AcceptTcpClientAsync().ConfigureAwait(false));
                }
                // Catches both ObjectDisposedException and InvalidOperationException
                // only when cancellation was requested.
                catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
                {
                    // Either _listener.Start wasn't called (a bug!)
                    // or the CancellationToken was canceled before
                    // we started accepting (giving an InvalidOperationException),
                    // or the CancellationToken was canceled after
                    // we started accepting (giving an ObjectDisposedException).
                    //
                    // In the latter two cases we should surface the cancellation
                    // exception, or otherwise re-throw the original exception.
                    // Re-throwing is done by filtering out the exception with when
                    // condition on catch side, see above.
                    // So the original stack trace isn't cluttered by a real re-throw.
                    //see: https://stackoverflow.com/questions/19220957/tcplistener-how-to-stop-listening-while-awaiting-accepttcpclientasync
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }
}