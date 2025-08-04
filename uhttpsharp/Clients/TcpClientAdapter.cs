using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace uhttpsharp.Clients
{
    public class TcpClientAdapter : IClient, IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public TcpClientAdapter(TcpClient client)
        {
            _client = client;
            _stream = _client.GetStream();

            // The next lines are commented out because they caused exceptions, 
            // They have been added because .net doesn't allow me to wait for data (ReadAsyncBlock).
            // Instead, I've added Task.Delay in MyStreamReader.ReadBuffer when
            // Read returns without data.

            // See https://github.com/Code-Sharp/uHttpSharp/issues/14

            // Read Timeout of one second.
            // _stream.ReadTimeout = 1000;
        }

        public Stream Stream
        {
            get { return _stream; }
        }

        public bool Connected
        {
            get { return _client.Connected; }
        }

        public void Close()
        {
            _client.Close();
        }


        public EndPoint RemoteEndPoint
        {
            get { return _client.Client.RemoteEndPoint; }
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_stream != null)
                        _stream.Dispose();
                    else
                        _client.Dispose();
                }

                disposedValue = true;
            }
        }

        ~TcpClientAdapter()
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