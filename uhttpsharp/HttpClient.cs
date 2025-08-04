/*
 * Copyright (C) 2011 uhttpsharp project - http://github.com/raistlinthewiz/uhttpsharp
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.

 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.

 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 */

using System.Text;
using System.Net;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using uhttpsharp.Clients;
using uhttpsharp.Headers;
using uhttpsharp.RequestProviders;
using uhttpsharp.Logging;
using System.Threading;
using System.Net.Security;

namespace uhttpsharp
{
    internal sealed class HttpClientHandler
    {
        private const string CrLf = "\r\n";
        private static readonly byte[] CrLfBuffer = Encoding.UTF8.GetBytes(CrLf);

        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private readonly IClient _client;
        private readonly Func<IHttpContext, Task> _requestHandler;
        private readonly IHttpRequestProvider _requestProvider;
        private readonly EndPoint _remoteEndPoint;
        private DateTime _lastOperationTime;
        private Stream _stream;

        public HttpClientHandler(IClient client, Func<IHttpContext, Task> requestHandler, IHttpRequestProvider requestProvider)
        {
            _remoteEndPoint = client.RemoteEndPoint;
            _client = client;
            _requestHandler = requestHandler;
            _requestProvider = requestProvider;

            Logger.InfoFormat("Got Client {0}", _remoteEndPoint);

            Task.Run(Process);

            UpdateLastOperationTime();
        }

        private async Task InitializeStream()
        {
            if (Client is ClientSslDecorator)
            {
                await ((ClientSslDecorator)Client).AuthenticateAsServer().ConfigureAwait(false);
                var sslstream = (SslStream)_client.Stream;
                if (!sslstream.IsAuthenticated)
                {
                    //the following throws InvalidOperationException with more specific message for not being authenticated
                    //for error codes see: https://learn.microsoft.com/de-de/windows/win32/secauthn/schannel-error-codes-for-tls-and-ssl-alerts
                    var cert = sslstream.RemoteCertificate;
                }
            }

            _stream = new BufferedStream(_client.Stream, 8192);
        }

        private async Task Process()
        {
            try
            {
                await InitializeStream();

                bool keepAlive = false;
                int retryGetRequestCount = 0;

                while (_client.Connected)
                {
                    // TODO : Configuration.
                    //As long as there are no limits applied, don't use the LimitedStream wrapper at all.
                    //new LimitedStream(_stream)
                    var wrappedStream = new NotFlushingStream(_stream);

                    var request = await _requestProvider.Provide(new MyStreamReader(wrappedStream)).ConfigureAwait(false);

                    if (request != null)
                    {
                        retryGetRequestCount = 0;
                        UpdateLastOperationTime();

                        var context = new HttpContext(request, _client.RemoteEndPoint);

                        Logger.InfoFormat("{1} : Got request {0}", request.Uri, _client.RemoteEndPoint);


                        await _requestHandler(context).ConfigureAwait(false);

                        if (context.Response != null)
                        {
                            var streamWriter = new StreamWriter(wrappedStream) { AutoFlush = false };
                            streamWriter.NewLine = "\r\n";
                            await WriteResponse(context, streamWriter).ConfigureAwait(false);
                            await wrappedStream.ExplicitFlushAsync().ConfigureAwait(false);

                            if (!request.KeepAliveConnection() || context.Response.CloseConnection)
                            {
                                _client.Close();
                            }
                            else
                            {
                                keepAlive = true;
                                //TODO: Also use configuration for a keep-alive timeout and send 408 status back if the timeout was hit.
                                //see: https://en.wikipedia.org/wiki/HTTP_persistent_connection
                            }
                        }

                        UpdateLastOperationTime();
                    }
                    else if (!keepAlive)
                    {
                        _client.Close();
                    }
                    else
                    {
                        // Fix for 100% CPU
                        await Task.Delay(100).ConfigureAwait(false);
                        if (++retryGetRequestCount >= 10)
                            keepAlive = false;
                    }
                }
            }
            catch (Exception e)
            {
                // Hate people who make bad calls.
                Logger.WarnException(string.Format("Error while serving : {0}", _remoteEndPoint), e);
                _client.Close();
            }

            Logger.InfoFormat("Lost Client {0}", _remoteEndPoint);
        }
        private async Task WriteResponse(HttpContext context, StreamWriter writer)
        {
            IHttpResponse response = context.Response;
            IHttpRequest request = context.Request;

            // Headers
            await writer.WriteLineAsync(string.Format("{0} {1} {2}",
                string.IsNullOrEmpty(request.Protocol) ? "HTTP/1.1" : request.Protocol,
                (int)response.ResponseCode,
                response.ResponseCode))
                .ConfigureAwait(false);

            foreach (var header in response.Headers)
            {
                await writer.WriteLineAsync(string.Format("{0}: {1}", header.Key, header.Value)).ConfigureAwait(false);
            }

            // Cookies
            if (context.Cookies.Touched)
            {
                await writer.WriteAsync(context.Cookies.ToCookieData())
                    .ConfigureAwait(false);
            }

            // Empty Line
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            // Body
            await response.WriteBody(writer).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

        }

        public IClient Client
        {
            get { return _client; }
        }

        public void ForceClose()
        {
            _client.Close();
        }

        public DateTime LastOperationTime
        {
            get
            {
                return _lastOperationTime;
            }
        }

        private void UpdateLastOperationTime()
        {
            _lastOperationTime = DateTime.Now;
        }

    }

    internal class NotFlushingStream : Stream
    {
        private readonly Stream _child;
        public NotFlushingStream(Stream child)
        {
            _child = child;
        }

        public override void Close()
        {
            _child.Close();
        }

        public void ExplicitFlush()
        {
            _child.Flush();
        }

        public Task ExplicitFlushAsync()
        {
            return _child.FlushAsync();
        }

        public override void Flush()
        {
            // _child.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _child.Seek(offset, origin);
        }
        public override void SetLength(long value)
        {
            _child.SetLength(value);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            return _child.Read(buffer, offset, count);
        }

        public override int ReadByte()
        {
            return _child.ReadByte();
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            _child.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value)
        {
            _child.WriteByte(value);
        }
        public override bool CanRead
        {
            get { return _child.CanRead; }
        }
        public override bool CanSeek
        {
            get { return _child.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return _child.CanWrite; }
        }
        public override bool CanTimeout
        {
            get { return _child.CanTimeout; }
        }
        public override long Length
        {
            get { return _child.Length; }
        }
        public override long Position
        {
            get { return _child.Position; }
            set { _child.Position = value; }
        }
        public override int ReadTimeout
        {
            get { return _child.ReadTimeout; }
            set { _child.ReadTimeout = value; }
        }
        public override int WriteTimeout
        {
            get { return _child.WriteTimeout; }
            set { _child.WriteTimeout = value; }
        }

        #region async overrides

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return _child.BeginRead(buffer, offset, count, callback, state);
        }
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return _child.BeginWrite(buffer, offset, count, callback, state);
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            return _child.CopyToAsync(destination, bufferSize, cancellationToken);
        }
        public override int EndRead(IAsyncResult asyncResult)
        {
            return _child.EndRead(asyncResult);
        }
        public override void EndWrite(IAsyncResult asyncResult)
        {
            _child.EndWrite(asyncResult);
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _child.ReadAsync(buffer, offset, count, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _child.WriteAsync(buffer, offset, count, cancellationToken);
        }

        #endregion
    }

    public static class RequestHandlersAggregateExtensions
    {

        public static Func<IHttpContext, Task> Aggregate(this IList<IHttpRequestHandler> handlers)
        {
            return handlers.Aggregate(0);
        }

        private static Func<IHttpContext, Task> Aggregate(this IList<IHttpRequestHandler> handlers, int index)
        {
            if (index == handlers.Count)
            {
                return null;
            }

            var currentHandler = handlers[index];
            var nextHandler = handlers.Aggregate(index + 1);

            return context => currentHandler.Handle(context, () => nextHandler != null ? nextHandler(context) : Task.CompletedTask);
        }


    }
}
