using System;

namespace uhttpsharp
{
    public interface IHttpProtocolVersionProvider
    {
        Version Provide(string protocolString);
    }
}
