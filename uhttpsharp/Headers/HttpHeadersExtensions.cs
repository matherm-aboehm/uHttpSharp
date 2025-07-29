using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using uhttpsharp.Logging;

namespace uhttpsharp.Headers
{
    public static class HttpHeadersExtensions
    {
        public static bool KeepAliveConnection(this IHttpHeaders headers)
        {
            string value;
            return headers.TryGetByName("connection", out value)
                && value.Equals("Keep-Alive", StringComparison.InvariantCultureIgnoreCase);
        }

        internal static bool IsMultipartContent(this IHttpHeaders headers, out string subType, out string boundary)
        {
            const string mimeType = "multipart/";
            const string boundaryParamName = "boundary=";
            subType = null;
            boundary = null;
            if (headers.TryGetByName("content-type", out var contentType) && contentType.StartsWith(mimeType, StringComparison.InvariantCultureIgnoreCase))
            {
                var splitterIndex = contentType.IndexOf(';');
                subType = splitterIndex != -1 ? contentType.Substring(mimeType.Length, splitterIndex - mimeType.Length) : contentType.Substring(mimeType.Length);
                subType = subType.TrimEnd();
                var attrText = splitterIndex != -1 ? contentType.Substring(splitterIndex + 1).TrimStart() : null;
                if (!string.IsNullOrEmpty(attrText) && attrText.StartsWith(boundaryParamName, StringComparison.InvariantCultureIgnoreCase))
                {
                    boundary = attrText.Substring(boundaryParamName.Length);
                    if (boundary[0] == '"')
                        boundary = boundary.UnquoteHeader();
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetByName<T>(this IHttpHeaders headers, string name, out T value)
        {
            string stringValue;

            if (headers.TryGetByName(name, out stringValue))
            {
                if (typeof(T) == typeof(string))
                    value = (T)(object)stringValue;
                if (typeof(T) == typeof(byte[]))
                    value = (T)(object)Convert.FromBase64String(stringValue);
                else
                    value = (T)Convert.ChangeType(stringValue, typeof(T));
                return true;
            }

            value = default(T);
            return false;
        }

        public static T GetByName<T>(this IHttpHeaders headers, string name)
        {
            T value;
            headers.TryGetByName(name, out value);
            return value;
        }

        public static T GetByNameOrDefault<T>(this IHttpHeaders headers, string name, T defaultValue)
        {
            T value;
            if (headers.TryGetByName(name, out value))
            {
                return value;
            }

            return defaultValue;
        }

        public static bool TryGetMultipartItemByName(this IHttpHeaders headers, string name, out IHttpPostWithBodyPart item)
        {
            if (headers is MultipartHttpHeaders multipartItems)
            {
                return multipartItems.TryGetItemByName(name, out item);
            }
            item = null;
            return false;
        }

        public static IEnumerable<KeyValuePair<string, IHttpPostWithBodyPart>> GetAllItemsWithName(this IHttpHeaders headers)
        {
            if (headers is MultipartHttpHeaders multipartItems)
            {
                return multipartItems.GetNamedItems();
            }
            return Enumerable.Empty<KeyValuePair<string, IHttpPostWithBodyPart>>();
        }

        public static IHttpPost GetNestedMultipart(this IHttpPostWithBodyPart bodyPart, ILog logger = null)
        {
            if (bodyPart.Parsed.IsMultipartContent(out var subType, out var boundary) && subType.Equals("mixed", StringComparison.InvariantCultureIgnoreCase))
            {
                return new HttpMultipartPost(bodyPart.Body, boundary, logger);
            }
            return null;
        }

        internal static string ConvertBodyPartItemToString(this IHttpPostWithBodyPart item)
        {
            if (item.Parsed.TryGetByName("content-type", out var contentType))
            {
                var (ctValue, ctAttr) = contentType.SplitHeaderValue();
                if (ctValue.StartsWith("text/", StringComparison.InvariantCultureIgnoreCase))
                {
                    //TODO: Implement quoted printable decoding
                    //Content-Transfer-Encoding: quoted-printable
                    Encoding textEncoding = Encoding.UTF8;
                    if (ctAttr.TryGetValue("charset", out var charset))
                        textEncoding = Encoding.GetEncoding(charset);
                    return textEncoding.GetString(item.Body);
                }
            }
            return Convert.ToBase64String(item.Body);
        }

        public static string ToUriData(this IHttpHeaders headers)
        {
            var builder = new StringBuilder();

            foreach (var header in headers)
            {
                builder.AppendFormat("{0}={1}&", Uri.EscapeDataString(header.Key), Uri.EscapeDataString(header.Value));
            }

            return builder.Length > 0 ? builder.ToString(0, builder.Length - 1) : string.Empty;
        }

        internal static string UnquoteHeader(this string header)
        {
            return UnquoteHeader(header, out var _);
        }

        internal static string UnquoteHeader(this string header, out int posEndingQuote)
        {
            var result = new StringBuilder(header.Length);
            bool firstQuoteFound = false;
            posEndingQuote = -1;
            for (int i = 0; i < header.Length; i++)
            {
                char c = header[i];
                if (c == '"')
                {
                    if (!firstQuoteFound)
                        firstQuoteFound = true;
                    else
                    {
                        posEndingQuote = i;
                        break;
                    }
                }
                else if (firstQuoteFound)
                {
                    if (c == '\\')
                    {
                        ++i;
                        if (i >= header.Length)
                            throw new FormatException("quoted header string unexpectedly ended on escape sequence");
                        result.Append(header[++i]);
                    }
                    else
                        result.Append(c);
                }
                else if (!Char.IsWhiteSpace(c))
                    break;
            }
            if (!firstQuoteFound)
                return header;
            if (firstQuoteFound && posEndingQuote == -1)
                throw new FormatException("quoted header string is missing the ending quotation mark");
            return result.ToString();
        }

        internal static KeyValuePair<string, string> SplitHeader(this string header)
        {
            var index = header.IndexOf(": ", StringComparison.InvariantCultureIgnoreCase);
            return new KeyValuePair<string, string>(header.Substring(0, index), header.Substring(index + 2));
        }

        public static (string, IDictionary<string, string>) SplitHeaderValue(this string headerValue)
        {
            string baseValue = headerValue.UnquoteHeader(out var posEndingQuote);
            var splitterIndex = headerValue.IndexOf(";", posEndingQuote + 1);
            if (splitterIndex == -1)
                return (baseValue, new Dictionary<string, string>());
            baseValue = headerValue.Substring(0, splitterIndex);
            var pos = splitterIndex + 1;
            int equalSignIndex;
            Dictionary<string, string> attrs = new Dictionary<string, string>();
            while ((equalSignIndex = headerValue.IndexOf('=', pos)) != -1)
            {
                var name = headerValue.Substring(pos, equalSignIndex - pos).Trim();
                pos = equalSignIndex + 1;
                var value = headerValue.Substring(pos).UnquoteHeader(out posEndingQuote);
                if (posEndingQuote == -1)
                {
                    splitterIndex = headerValue.IndexOf(';', pos);
                    if (splitterIndex == -1)
                    {
                        pos = headerValue.Length;
                    }
                    else
                    {
                        value = headerValue.Substring(pos, splitterIndex);
                        pos = splitterIndex + 1;
                    }
                }
                else
                {
                    pos += posEndingQuote + 1;
                    splitterIndex = headerValue.IndexOf(';', pos);
                    if (splitterIndex == -1)
                        pos = headerValue.Length;
                    else
                        pos = splitterIndex + 1;
                }
                attrs.Add(name, value);
            }
            return (baseValue, attrs);
        }

        private static class EmptyArraySegment<T>
        {
            public static ArraySegment<T> Instance { get; } = new ArraySegment<T>(new T[0]);
        }

        internal static byte[] ToArray(this ArraySegment<byte> seg)
        {
            if (seg.Array == null)
                throw new InvalidOperationException("array segment has default value");

            if (seg.Count == 0)
                return EmptyArraySegment<byte>.Instance.Array;

            byte[] result = new byte[seg.Count];
            Buffer.BlockCopy(seg.Array, seg.Offset, result, 0, result.Length);
            return result;
        }

        internal static ArraySegment<T> Slice<T>(this ArraySegment<T> seg, int index)
        {
            if (seg.Array == null)
                throw new InvalidOperationException("array segment has default value");

            if ((uint)index > (uint)seg.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "index must be less or equal to the count of array segment");

            return new ArraySegment<T>(seg.Array, seg.Offset + index, seg.Count - index);
        }
    }
}