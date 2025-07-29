using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace uhttpsharp.Headers
{
    internal class HttpHeadersDebuggerProxy
    {
        private readonly IHttpHeaders _real;

        [DebuggerDisplay("{Value,nq}", Name = "{Key,nq}")]
        internal class HttpHeader
        {
            private readonly KeyValuePair<string, string> _header;
            public HttpHeader(KeyValuePair<string, string> header)
            {
                _header = header;
            }

            public string Value
            {
                get
                {
                    return _header.Value;
                }
            }

            public string Key
            {
                get
                {
                    return _header.Key;
                }
            }
        }

        public HttpHeadersDebuggerProxy(IHttpHeaders real)
        {
            _real = real;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public HttpHeader[] Headers
        {
            get
            {
                return _real.Select(kvp => new HttpHeader(kvp)).ToArray();
            }
        }

    }

    internal class MultipartHttpHeadersDebuggerProxy
    {
        private readonly MultipartHttpHeaders _real;

        [DebuggerDisplay("{Value}", Name = "{Key}")]
        internal class BodyPart
        {
            private readonly KeyValuePair<string, IHttpPostWithBodyPart> _bodyPart;
            public BodyPart(KeyValuePair<string, IHttpPostWithBodyPart> bodyPart)
            {
                _bodyPart = bodyPart;
            }

            public IHttpPostWithBodyPart Value
            {
                get { return _bodyPart.Value; }
            }

            internal string ValueAsString
            {
                get { return _bodyPart.Value.ConvertBodyPartItemToString(); }
            }

            public string Key
            {
                get { return _bodyPart.Key; }
            }
        }

        public MultipartHttpHeadersDebuggerProxy(MultipartHttpHeaders real)
        {
            _real = real;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public BodyPart[] Parts
        {
            get { return _real.GetNamedItems().Select(kvp => new BodyPart(kvp)).ToArray(); }
        }
    }

    internal class HttpPostWithBodyPartDebuggerProxy
    {
        private readonly IHttpPostWithBodyPart _real;

        public HttpPostWithBodyPartDebuggerProxy(IHttpPostWithBodyPart real)
        {
            _real = real;
        }

        public IHttpHeaders Headers { get { return _real.Parsed; } }

        public byte[] Body { get { return _real.Body; } }
    }
}