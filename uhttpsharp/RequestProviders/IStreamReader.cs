using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using uhttpsharp.Headers;

namespace uhttpsharp.RequestProviders
{
    public interface IStreamReader
    {
        int LastReadBytesCount { get; }

        Task<string> ReadLine();

        Task<byte[]> ReadBytes(int count);


    }
    class StreamReaderAdapter : IStreamReader
    {
        private readonly StreamReader _reader;
        public StreamReaderAdapter(StreamReader reader)
        {
            _reader = reader;
        }

        public int LastReadBytesCount
        {
            get { throw new NotImplementedException(); }
        }

        public async Task<string> ReadLine()
        {
            return await _reader.ReadLineAsync().ConfigureAwait(false);
        }
        public async Task<byte[]> ReadBytes(int count)
        {
            var tempBuffer = new char[count];

            await _reader.ReadBlockAsync(tempBuffer, 0, count).ConfigureAwait(false);

            var retVal = new byte[count];

            for (int i = 0; i < tempBuffer.Length; i++)
            {
                retVal[i] = (byte)tempBuffer[i];
            }

            return retVal;
        }
    }

    class MyStreamReader : IStreamReader
    {
        private static readonly Encoding SafeAsciiEncoding = Encoding.GetEncoding(Encoding.ASCII.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private const int BufferSize = 8192 / 4;
        private readonly Stream _underlyingStream;
        private readonly Encoding _encoding;

        private readonly byte[] _middleBuffer = new byte[BufferSize];
        private int _index;
        private int _count;
        private int _lastReadBytesCount;

        public MyStreamReader(Stream underlyingStream, Encoding encoding = null)
        {
            _underlyingStream = underlyingStream;
            _encoding = encoding ?? SafeAsciiEncoding;
        }

        public int LastReadBytesCount
        {
            get { return _lastReadBytesCount; }
        }

        private async Task<bool> ReadBuffer()
        {
            _count = await _underlyingStream.ReadAsync(_middleBuffer, 0, BufferSize).ConfigureAwait(false);

            _index = 0;
            return _count != 0;
        }

        public async Task<string> ReadLine()
        {
            //HINT: Using plain StringBuilder would be faster but doesn't check for encoding errors.
            //Using StringBuilder in combination with Encoding.GetString() for each character would be much slower
            //than doing it only once for byte array, so instead using MemoryStream for buffer.
            var builder = new MemoryStream(64);

            _lastReadBytesCount = 0;
            if (_index == _count)
            {
                if (!await ReadBuffer().ConfigureAwait(false))
                    return string.Empty;
            }
            int startIndex = _index;
            var readByte = _middleBuffer[_index++];
            byte lastByte = 0;

            while (readByte != '\n' && lastByte != '\r')
            {
                lastByte = readByte;

                if (_index == _count)
                {
                    _lastReadBytesCount += _count - startIndex;
                    builder.Write(_middleBuffer, startIndex, _count - startIndex - (lastByte == '\r' ? 1 : 0));
                    startIndex = 0;
                    if (!await ReadBuffer().ConfigureAwait(false))
                        break;
                }
                readByte = _middleBuffer[_index++];
            }
            int count = _count == 0 ? 0 : _index - 1;
            _lastReadBytesCount += _index - startIndex;

            //Debug.WriteLine("Readline : " + sw.ElapsedMilliseconds);

            if (lastByte == '\r' && count > 0)
                count--;
            if (builder.Length == 0)
                if ((count - startIndex) == 0)
                    return string.Empty;
                else
                    return _encoding.GetString(_middleBuffer, startIndex, count - startIndex);
            else if ((count - startIndex) != 0)
                builder.Write(_middleBuffer, startIndex, count - startIndex);
            return _encoding.GetString(builder.ToArray());
        }

        public async Task<byte[]> ReadBytes(int count)
        {
            bool continueReading = false; // means reading a fixed count of bytes
            if (count == -1)
            {
                count = BufferSize;
                continueReading = true;
            }
            var buffer = new byte[count];
            int currentByte = 0;

            // Empty the buffer
            int bytesToRead = Math.Min(_count - _index, count);
            if (bytesToRead != 0)
            {
                Buffer.BlockCopy(_middleBuffer, _index, buffer, 0, bytesToRead);
                currentByte += bytesToRead;
            }

            _index = _count;

            // Read from stream
            do
            {
                while (currentByte < count)
                {
                    var readCount = await _underlyingStream.ReadAsync(buffer, currentByte, count - currentByte).ConfigureAwait(false);
                    if (readCount == 0)
                    {
                        if (!continueReading)
                            throw new IOException($"Unexpectedly reached end of stream. {(count - currentByte)} bytes not received.");
                        else
                        {
                            continueReading = false;
                            break;
                        }
                    }
                    currentByte += readCount;
                }
                if (continueReading)
                {
                    count += BufferSize;
                    byte[] oldbuffer = buffer;
                    buffer = new byte[count];
                    Buffer.BlockCopy(oldbuffer, 0, buffer, 0, oldbuffer.Length);
                }
            } while (continueReading);

            //Debug.WriteLine("ReadBytes(" + count + ") : " + sw.ElapsedMilliseconds);

            if (currentByte == count)
                return buffer;
            else
            {
                byte[] result = new byte[currentByte];
                Buffer.BlockCopy(buffer, 0, result, 0, currentByte);
                return result;
            }
        }
    }

    class ChunkedStreamReader : IStreamReader
    {
        private readonly IStreamReader _child;
        private readonly bool _allowTrailers;

        public ChunkedStreamReader(IStreamReader child, bool allowTrailers)
        {
            _child = child;
            _allowTrailers = allowTrailers;
        }

        public int LastReadBytesCount => _child.LastReadBytesCount;

        public Task<string> ReadLine()
        {
            return _child.ReadLine();
        }

        public async Task<byte[]> ReadBytes(int count)
        {
            if (count != -1)
                throw new ArgumentOutOfRangeException(nameof(count), count,
                    "ChunkedStreamReader doesn't currently support fixed-size reading. It can only read until the end of the stream.");
            MemoryStream buffer = new MemoryStream();
            string line;
            while (!string.IsNullOrEmpty(line = await _child.ReadLine().ConfigureAwait(false)))
            {
                var (chunkSizeString, chunkParams) = line.SplitHeaderValue();
                if (ulong.TryParse(chunkSizeString, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var chunkSize))
                {
                    if (chunkSize > 0)
                    {
                        var chunkBytes = await _child.ReadBytes((int)chunkSize).ConfigureAwait(false);
                        buffer.Write(chunkBytes, 0, chunkBytes.Length);
                        line = await _child.ReadLine().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(line) || _child.LastReadBytesCount != 2)
                            throw new InvalidOperationException("the chunk was not correctly terminated by CRLF");
                    }
                    else if (_allowTrailers)
                        break;
                }
            }
            return buffer.ToArray();
        }
    }
}