using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using uhttpsharp.Headers;
using uhttpsharp.Logging;

namespace uhttpsharp.RequestProviders
{
    public class HttpRequestProvider : IHttpRequestProvider
    {
        private static readonly char[] Separators = { '/' };

        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();
        
        public async Task<IHttpRequest> Provide(IStreamReader reader)
        {
            // parse the http request
            var request = await reader.ReadLine().ConfigureAwait(false);

            if (request == null)
                return null;

            var firstSpace = request.IndexOf(' ');
            var lastSpace = request.LastIndexOf(' ');

            var tokens = new []
            {
                request.Substring(0, firstSpace),
                request.Substring(firstSpace + 1, lastSpace - firstSpace - 1),
                request.Substring(lastSpace + 1)
            };

            if (tokens.Length != 3)
            {
                return null;
            }

            
            var httpProtocol = tokens[2];

            var url = tokens[1];
            var queryString = GetQueryStringData(ref url);
            var uri = new Uri(url, UriKind.Relative);

            var headersRaw = new List<KeyValuePair<string, string>>();

            // get the headers
            string line;

            while (!string.IsNullOrEmpty((line = await reader.ReadLine().ConfigureAwait(false))))
            {
                string currentLine = line;

                var headerKvp = currentLine.SplitHeader();
                headersRaw.Add(headerKvp);
            }

            IHttpHeaders headers = new HttpHeaders(headersRaw.ToDictionary(k => k.Key, k => k.Value, StringComparer.InvariantCultureIgnoreCase));
            IHttpPost post = await GetPostData(reader, headers).ConfigureAwait(false);

            string verb;
            if (!headers.TryGetByName("_method", out verb))
            {
                verb = tokens[0];
            }
            var httpMethod = HttpMethodProvider.Default.Provide(verb);
            return new HttpRequest(headers, httpMethod, httpProtocol, uri,
                uri.OriginalString.Split(Separators, StringSplitOptions.RemoveEmptyEntries), queryString, post);
        }

        private static IHttpHeaders GetQueryStringData(ref string url)
        {
            var queryStringIndex = url.IndexOf('?');
            IHttpHeaders queryString;
            if (queryStringIndex != -1)
            {
                queryString = new QueryStringHttpHeaders(url.Substring(queryStringIndex + 1));
                url = url.Substring(0, queryStringIndex);
            }
            else
            {
                queryString = EmptyHttpHeaders.Empty;
            }
            return queryString;
        }

        private static async Task<IHttpPost> GetPostData(IStreamReader streamReader, IHttpHeaders headers)
        {
            int postContentLength;
            string transferEncoding = null;
            IHttpPost post;
            //https://stackoverflow.com/questions/16339198/which-http-methods-require-a-body
            if ((headers.TryGetByName("content-length", out postContentLength) && postContentLength > 0) ||
                headers.TryGetByName("transfer-encoding", out transferEncoding))
            {
                bool allowTrailers = false;
                if (postContentLength == 0 && transferEncoding != null &&
                    transferEncoding.IndexOf("chunked", StringComparison.InvariantCultureIgnoreCase) != -1)
                {
                    postContentLength = -1;
                    if (headers.TryGetByName("te", out var acceptedTransferEncoding) &&
                        acceptedTransferEncoding.IndexOf("trailers", StringComparison.InvariantCultureIgnoreCase) != -1)
                        allowTrailers = true;
                    streamReader = new ChunkedStreamReader(streamReader, allowTrailers);
                }
                post = await HttpPost.Create(streamReader, postContentLength, Logger).ConfigureAwait(false);

                if (allowTrailers && headers is IHttpHeadersAppendable appendableHeaders)
                {
                    var headersRaw = new List<KeyValuePair<string, string>>();

                    // get the trailing headers
                    string line;
                    while (!string.IsNullOrEmpty(line = await streamReader.ReadLine().ConfigureAwait(false)))
                    {
                        headersRaw.Add(line.SplitHeader());
                    }

                    IHttpHeaders trailingHeaders = new HttpHeaders(headersRaw.ToDictionary(k => k.Key, k => k.Value, StringComparer.InvariantCultureIgnoreCase));
                    appendableHeaders.AppendHeaders(trailingHeaders);
                }
            }
            else
            {
                post = EmptyHttpPost.Empty;
            }
            return post;
        }
    }
}