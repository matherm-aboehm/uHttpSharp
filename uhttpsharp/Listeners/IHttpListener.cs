using System;
using System.Threading;
using System.Threading.Tasks;
using uhttpsharp.Clients;

namespace uhttpsharp.Listeners
{
    public interface IHttpListener : IDisposable
    {

        Task<IClient> GetClient(CancellationToken cancellationToken = default(CancellationToken));

    }
}