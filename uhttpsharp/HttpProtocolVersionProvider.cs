using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace uhttpsharp
{
    public class HttpProtocolVersionProvider : IHttpProtocolVersionProvider
    {
        public static readonly IHttpProtocolVersionProvider Default = new HttpProtocolVersionProviderCache(new HttpProtocolVersionProvider());
        private const string _prefix = "HTTP/";

        internal HttpProtocolVersionProvider()
        {

        }

        public Version Provide(string protocolString)
        {
            //see: https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/Evolution_of_HTTP#http0.9_%E2%80%93_the_one-line_protocol
            if (string.IsNullOrEmpty(protocolString))
                return new Version(0, 9);

            if (protocolString.StartsWith(_prefix, StringComparison.InvariantCultureIgnoreCase))
            {
                protocolString = protocolString.Substring(_prefix.Length);
                if (Version.TryParse(protocolString, out var version))
                {
                    if (version == HttpVersion.Version10)
                        return HttpVersion.Version10;
                    else if (version == HttpVersion.Version11)
                        return HttpVersion.Version11;

                    return version;
                }

                if (int.TryParse(protocolString, out int majorVersion))
                    return new Version(majorVersion, 0);
            }

            throw new FormatException($"The protocol string for HTTP request has a wrong format: {protocolString}");
        }
    }
}
