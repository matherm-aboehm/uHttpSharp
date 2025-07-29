using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using uhttpsharp.RequestProviders;
using uhttpsharp.Logging;
using System.Globalization;

namespace uhttpsharp.Headers
{
    internal class EmptyHttpPost : IHttpPost
    {
        private static readonly byte[] _emptyBytes = new byte[0];
        private static readonly Task<IHttpHeaders> _taskEmptyHeaders = Task.FromResult(EmptyHttpHeaders.Empty);

        public static readonly IHttpPost Empty = new EmptyHttpPost();

        private EmptyHttpPost()
        {

        }

        #region IHttpPost implementation

        public byte[] Raw
        {
            get
            {
                return _emptyBytes;
            }
        }

        public IHttpHeaders Parsed
        {
            get
            {
                return EmptyHttpHeaders.Empty;
            }
        }

        public Task<IHttpHeaders> ParsedAsync
        {
            get
            {
                return _taskEmptyHeaders;
            }
        }

        #endregion
    }

    internal class HttpPost : IHttpPost
    {
        public static async Task<IHttpPost> Create(IStreamReader reader, int postContentLength, ILog logger)
        {
            byte[] raw = await reader.ReadBytes(postContentLength).ConfigureAwait(false);
            return new HttpPost(raw, postContentLength);
        }

        private readonly int _readBytes;

        private readonly byte[] _raw;

        private readonly Lazy<IHttpHeaders> _parsed;

        private readonly Lazy<Task<IHttpHeaders>> _parsedAsync;

        public HttpPost(byte[] raw, int readBytes)
        {
            _raw = raw;
            _readBytes = readBytes;
            _parsed = new Lazy<IHttpHeaders>(Parse);
            _parsedAsync = _parsed.ToAsyncLazy();
        }

        private IHttpHeaders Parse()
        {
            string body = Encoding.UTF8.GetString(_raw, 0, _readBytes);
            var parsed = new QueryStringHttpHeaders(body);

            return parsed;
        }

        #region IHttpPost implementation

        public byte[] Raw
        {
            get
            {
                return _raw;
            }
        }

        public IHttpHeaders Parsed
        {
            get
            {
                return _parsed.Value;
            }
        }

        public Task<IHttpHeaders> ParsedAsync
        {
            get
            {
                return _parsedAsync.Value;
            }
        }

        #endregion


    }

    [DebuggerDisplay("{Parsed}, Body={ContentLength,d} bytes")]
    [DebuggerTypeProxy(typeof(HttpPostWithBodyPartDebuggerProxy))]
    internal class HttpMultipartBodyPart : IHttpPost, IHttpPostWithBodyPart
    {
        private readonly ArraySegment<byte> _rawSegment;

        private readonly int _contentOffset;

        private readonly IHttpHeaders _parsed;

        private readonly Task<IHttpHeaders> _parsedAsync;

        public HttpMultipartBodyPart(ArraySegment<byte> rawSegment, int contentOffset, IHttpHeaders parsedHeaders)
        {
            _rawSegment = rawSegment;
            _contentOffset = contentOffset;
            _parsed = parsedHeaders;
            _parsedAsync = Task.FromResult(parsedHeaders);
        }

        #region IHttpPost implementation

        public byte[] Raw
        {
            get
            {
                return _rawSegment.ToArray();
            }
        }

        public IHttpHeaders Parsed
        {
            get
            {
                return _parsed;
            }
        }

        public Task<IHttpHeaders> ParsedAsync
        {
            get
            {
                return _parsedAsync;
            }
        }

        #endregion

        #region IHttpPostWithBodyPart implementation

        public byte[] Body
        {
            get
            {
                return Content.ToArray();
            }
        }

        #endregion

        internal ArraySegment<byte> Content
        {
            get
            {
                return _rawSegment.Slice(_contentOffset);
            }
        }

        internal int ContentLength
        {
            get
            {
                return _rawSegment.Count - _contentOffset;
            }
        }

        public override string ToString()
        {
            return $"{Parsed}, Body={ContentLength} bytes";
        }
    }

    internal class HttpMultipartPost : IHttpPost
    {
        public static async Task<IHttpPost> Create(IStreamReader reader, int postContentLength, string boundary, ILog logger)
        {
            byte[] raw = await reader.ReadBytes(postContentLength).ConfigureAwait(false);
            return new HttpMultipartPost(raw, boundary, logger);
        }

        private readonly byte[] _raw;

        private readonly string _boundary;

        private readonly ILog _logger;

        private readonly Lazy<IHttpHeaders> _parsedBodyParts;

        private readonly Lazy<Task<IHttpHeaders>> _parsedBodyPartsAsync;


        public HttpMultipartPost(byte[] raw, string boundary, ILog logger)
        {
            _raw = raw;
            _boundary = boundary;
            _logger = logger;
            _parsedBodyPartsAsync = new Lazy<Task<IHttpHeaders>>(() => Task.Run(ParseAsync));
            _parsedBodyParts = _parsedBodyPartsAsync.FromAsyncLazy();
        }

        private async Task<IHttpHeaders> ParseAsync()
        {
            IStreamReader reader = new MyStreamReader(new MemoryStream(_raw), Encoding.UTF8);
            var delimiter = "--" + _boundary;
            var closeDelimiter = delimiter + "--";
            int position = 0;
            int positionBeforeNextDelimiter = 0;
            int positionBeforeHeader = 0;
            int positionAfterHeader = 0;
            Dictionary<string, IHttpPostWithBodyPart> multipartItems = new Dictionary<string, IHttpPostWithBodyPart>();
            List<KeyValuePair<string, string>> headersRaw = null;
            //see: https://www.ietf.org/rfc/rfc2046.txt
            async Task<bool> findNextDelimiter()
            {
                string candidateline;
                while (!(candidateline = await reader.ReadLine().ConfigureAwait(false)).StartsWith(delimiter) && reader.LastReadBytesCount != 0)
                    position += reader.LastReadBytesCount;

                //preceding CRLF is attached to the delimiter, so subtract 2 from the current position, but limiting it to the start of stream
                positionBeforeNextDelimiter = Math.Max(position - 2, 0);
                position += reader.LastReadBytesCount;
                if (headersRaw != null)
                {
                    var headers = new HttpHeaders(headersRaw.ToDictionary(k => k.Key, k => k.Value, StringComparer.InvariantCultureIgnoreCase));
                    string partName = multipartItems.Count.ToString(CultureInfo.InvariantCulture);
                    if (headers.TryGetByName("content-disposition", out var contentDispo))
                    {
                        var (contentDispoBase, contentDispoAttrs) = contentDispo.SplitHeaderValue();
                        if (contentDispoBase.Equals("form-data", StringComparison.InvariantCultureIgnoreCase) &&
                            contentDispoAttrs.TryGetValue("name", out var nameAttr))
                        {
                            partName = nameAttr;
                        }
                        else if (contentDispoBase.Equals("file", StringComparison.InvariantCultureIgnoreCase) &&
                            contentDispoAttrs.TryGetValue("filename", out var filenameAttr))
                        {
                            partName = filenameAttr;
                        }
                    }
                    var bodyPartRaw = new ArraySegment<byte>(_raw, positionBeforeHeader, positionBeforeNextDelimiter - positionBeforeHeader);
                    multipartItems.Add(partName, new HttpMultipartBodyPart(bodyPartRaw, positionAfterHeader - positionBeforeHeader, headers));
                }
                return reader.LastReadBytesCount != 0 && (candidateline.Length < (delimiter.Length + 2)
                    || candidateline[delimiter.Length] != '-' || candidateline[delimiter.Length + 1] != '-');
            }
            while (await findNextDelimiter().ConfigureAwait(false))
            {
                positionBeforeHeader = position;
                headersRaw = new List<KeyValuePair<string, string>>();
                string line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLine().ConfigureAwait(false)))
                {
                    position += reader.LastReadBytesCount;
                    var headerKvp = line.SplitHeader();
                    headersRaw.Add(headerKvp);
                }
                position += reader.LastReadBytesCount;
                positionAfterHeader = position;
            }
            if (position < _raw.Length)
            {
                _logger?.InfoFormat("There are {0} epilogue bytes after last boundary delimiter in multipart message.", _raw.Length - position);
            }
            return new MultipartHttpHeaders(multipartItems);
        }

        #region IHttpPost implementation

        public byte[] Raw
        {
            get
            {
                return _raw;
            }
        }

        public IHttpHeaders Parsed
        {
            get
            {
                return _parsedBodyParts.Value;
            }
        }

        public Task<IHttpHeaders> ParsedAsync
        {
            get
            {
                return _parsedBodyPartsAsync.Value;
            }
        }

        #endregion
    }

    [DebuggerDisplay("{Count,d} Parts")]
    [DebuggerTypeProxy(typeof(MultipartHttpHeadersDebuggerProxy))]
    internal class MultipartHttpHeaders : IHttpHeaders
    {
        private readonly IDictionary<string, IHttpPostWithBodyPart> _multipartItems;

        public MultipartHttpHeaders(IDictionary<string, IHttpPostWithBodyPart> multipartItems)
        {
            _multipartItems = multipartItems;
        }

        public string GetByName(string name)
        {
            var item = _multipartItems[name];
            return item.ConvertBodyPartItemToString();
        }

        public bool TryGetByName(string name, out string value)
        {
            if (_multipartItems.TryGetValue(name, out var item))
            {
                value = item.ConvertBodyPartItemToString();
                return true;
            }
            value = default(string);
            return false;
        }

        internal bool TryGetItemByName(string name, out IHttpPostWithBodyPart item)
        {
            return _multipartItems.TryGetValue(name, out item);
        }

        internal IEnumerable<KeyValuePair<string, IHttpPostWithBodyPart>> GetNamedItems()
        {
            return _multipartItems;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _multipartItems.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value.ConvertBodyPartItemToString())).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        internal int Count
        {
            get
            {
                return _multipartItems.Count;
            }
        }

        public override string ToString()
        {
            return $"{Count} Parts";
        }
    }

    [DebuggerDisplay("{Count,d} Headers")]
    [DebuggerTypeProxy(typeof(HttpHeadersDebuggerProxy))]
    public class ListHttpHeaders : IHttpHeaders
    {
        private readonly IList<KeyValuePair<string, string>> _values;
        public ListHttpHeaders(IList<KeyValuePair<string, string>> values)
        {
            _values = values;
        }

        public string GetByName(string name)
        {
            return _values.Where(kvp => kvp.Key.Equals(name, StringComparison.InvariantCultureIgnoreCase)).Select(kvp => kvp.Value).First();
        }

        public bool TryGetByName(string name, out string value)
        {
            value = _values.Where(kvp => kvp.Key.Equals(name, StringComparison.InvariantCultureIgnoreCase)).Select(kvp => kvp.Value).FirstOrDefault();

            return (value != default(string));
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        internal int Count
        {
            get
            {
                return _values.Count;
            }
        }

        public override string ToString()
        {
            return $"{Count} Headers";
        }
    }


    [DebuggerDisplay("{Count,d} Headers")]
    [DebuggerTypeProxy(typeof(HttpHeadersDebuggerProxy))]
    public class HttpHeaders : IHttpHeaders, IHttpHeadersAppendable
    {
        private readonly IDictionary<string, string> _values;

        public HttpHeaders(IDictionary<string, string> values)
        {
            _values = values;
        }

        public string GetByName(string name)
        {
            return _values[name];
        }
        public bool TryGetByName(string name, out string value)
        {
            return _values.TryGetValue(name, out value);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void AppendHeaders(IHttpHeaders headers)
        {
            foreach (var kvp in headers)
            {
                _values.Add(kvp);
            }
        }

        internal int Count
        {
            get
            {
                return _values.Count;
            }
        }

        public override string ToString()
        {
            return $"{Count} Headers";
        }
    }
}