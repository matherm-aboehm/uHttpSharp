using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

        public static bool TryGetByName<T>(this IHttpHeaders headers, string name, out T value)
        {
            string stringValue;
            
            if (headers.TryGetByName(name, out stringValue))
            {
                value = (T) Convert.ChangeType(stringValue, typeof(T));
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
    }
}