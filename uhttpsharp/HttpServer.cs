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

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using uhttpsharp.Listeners;
using uhttpsharp.RequestProviders;
using uhttpsharp.Logging;
using System.Threading;

namespace uhttpsharp
{
    public sealed class HttpServer : IDisposable, IAsyncDisposable
    {
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private bool _isActive;
        private bool _isDisposed;

        private readonly IList<IHttpRequestHandler> _handlers = new List<IHttpRequestHandler>();
        private readonly IList<(IHttpListener, bool)> _listeners = new List<(IHttpListener, bool)>();
        private readonly IList<Task> _runningListeners = new List<Task>();
        private readonly CancellationTokenSource _ctsDispose = new CancellationTokenSource();
        private readonly IHttpRequestProvider _requestProvider;


        public HttpServer(IHttpRequestProvider requestProvider)
        {
            _requestProvider = requestProvider;
        }

        public void Use(IHttpRequestHandler handler)
        {
            _handlers.Add(handler);
        }

        public void Use(IHttpListener listener, bool ownsListener = true)
        {
            _listeners.Add((listener, ownsListener));
        }

        public void Start()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(HttpServer));

            _isActive = true;

            foreach (var (listener, ownsListener) in _listeners)
            {
                IHttpListener tempListener = listener;

                var task = Task.Run(() => Listen(tempListener, _ctsDispose.Token), _ctsDispose.Token);
                if (ownsListener)
                    task = task.ContinueWith((_) => tempListener.Dispose());
                _runningListeners.Add(task);
            }

            Logger.InfoFormat("Embedded uhttpserver started.");
        }

        private async Task Listen(IHttpListener listener, CancellationToken cancellationToken)
        {
            var aggregatedHandler = _handlers.Aggregate();

            while (_isActive)
            {
                try
                {
                    new HttpClientHandler(await listener.GetClient(cancellationToken).ConfigureAwait(false), aggregatedHandler, _requestProvider);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
                {
                    // ignore when it was canceled by _ctsDispose
                }
                catch (Exception e)
                {
                    Logger.WarnException("Error while getting client", e);
                }
            }

            Logger.InfoFormat("Embedded uhttpserver stopped.");
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                _isActive = false;
                _isDisposed = true;
                _ctsDispose.Cancel();

                if (disposing)
                {
                    var awaitAllTask = Task.WhenAll(_runningListeners);
                    if (!awaitAllTask.IsCompleted)
                        awaitAllTask.RunSynchronously();
                    //HINT: Calling Wait() is needed even though RunSynchronously() was called, so exceptions are fired.
                    //see remarks: https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.runsynchronously?view=net-9.0
                    awaitAllTask.Wait();
                    _ctsDispose.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        //see: https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync
        public async ValueTask DisposeAsync()
        {
            Dispose(false);
            await Task.WhenAll(_runningListeners).ConfigureAwait(false);
            _ctsDispose.Dispose();
            GC.SuppressFinalize(this);
        }

        ~HttpServer()
        {
            Dispose(false);
        }
    }
}