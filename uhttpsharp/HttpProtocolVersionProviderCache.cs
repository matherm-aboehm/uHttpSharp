using System;
using System.Collections.Concurrent;

namespace uhttpsharp
{
    class HttpProtocolVersionProviderCache : IHttpProtocolVersionProvider
    {
        private readonly ConcurrentDictionary<string, Version> _cache = new ConcurrentDictionary<string, Version>(StringComparer.InvariantCultureIgnoreCase);

        private readonly Func<String, Version> _childProvide;
        public HttpProtocolVersionProviderCache(IHttpProtocolVersionProvider child)
        {
            _childProvide = child.Provide;
        }
        public Version Provide(string protocolString)
        {
            return _cache.GetOrAdd(protocolString, _childProvide);
        }
    }
}
