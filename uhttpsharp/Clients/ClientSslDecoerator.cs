using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace uhttpsharp.Clients
{
    public class ClientSslDecorator : IClient, IDisposable
    {
        private readonly IClient _child;
        private readonly X509Certificate _certificate;
        private readonly SslStream _sslStream;

        public ClientSslDecorator(IClient child, X509Certificate certificate)
        {
            _child = child;
            _certificate = certificate;
            _sslStream = new SslStream(_child.Stream);
        }

        public async Task AuthenticateAsServer()
        {
            Task timeout = Task.Delay(TimeSpan.FromSeconds(10));
            var completedTask = await Task.WhenAny(_sslStream.AuthenticateAsServerAsync(_certificate, false, SslProtocols.None, true), timeout).ConfigureAwait(false);
            if (timeout == completedTask)
            {
                throw new TimeoutException("SSL Authentication Timeout");
            }
            //HINT: Propagate exception, if it was a faulting task. Task.WhenAny does not do this on its own.
            await completedTask;
        }

        public Stream Stream
        {
            get { return _sslStream; }
        }

        public bool Connected
        {
            get { return _child.Connected; }
        }

        public void Close()
        {
            _child.Close();
        }

        public EndPoint RemoteEndPoint
        {
            get { return _child.RemoteEndPoint; }
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_sslStream != null)
                        _sslStream.Dispose();
                    else
                        (_child as IDisposable)?.Dispose();
                }

                disposedValue = true;
            }
        }

        ~ClientSslDecorator()
        {
            Dispose(false);
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}