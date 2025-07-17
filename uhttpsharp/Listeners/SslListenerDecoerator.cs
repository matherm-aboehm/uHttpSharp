using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using uhttpsharp.Clients;

namespace uhttpsharp.Listeners
{
    public class ListenerSslDecorator : IHttpListener
    {
        private readonly IHttpListener _child;
        private readonly X509Certificate _certificate;

        public ListenerSslDecorator(IHttpListener child, X509Certificate certificate)
        {
            _child = child;
            _certificate = certificate;
        }

        public async Task<IClient> GetClient(CancellationToken cancellationToken = default(CancellationToken))
        {
            return new ClientSslDecorator(await _child.GetClient(cancellationToken).ConfigureAwait(false), _certificate);
        }

        public void Dispose()
        {
            _child.Dispose();
        }
    }
}
