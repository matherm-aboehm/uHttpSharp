using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace uhttpsharp.RequestProviders
{
    public interface IStreamReader
    {

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

        public MyStreamReader(Stream underlyingStream, Encoding encoding = null)
        {
            _underlyingStream = underlyingStream;
            _encoding = encoding ?? SafeAsciiEncoding;
        }

        private async Task<bool> ReadBuffer()
        {
            //do
            //{
            _count = await _underlyingStream.ReadAsync(_middleBuffer, 0, BufferSize).ConfigureAwait(false);

            /*if (_count == 0)
            {
                // Fix for 100% CPU
                await Task.Delay(100).ConfigureAwait(false);
            }
            }
            while (_count == 0);*/

            _index = 0;
            return _count != 0;
        }

        public async Task<string> ReadLine()
        {
            //HINT: Using plain StringBuilder would be faster but doesn't check for encoding errors.
            //Using StringBuilder in combination with Encoding.GetString() for each character would be much slower
            //than doing it only once for byte array, so instead using MemoryStream for buffer.
            var builder = new MemoryStream(64);

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
                    builder.Write(_middleBuffer, startIndex, _count - startIndex - (lastByte == '\r' ? 1 : 0));
                    startIndex = 0;
                    if (!await ReadBuffer().ConfigureAwait(false))
                        break;
                }
                readByte = _middleBuffer[_index++];
            }
            int count = _count == 0 ? 0 : _index - 1;

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
            var buffer = new byte[count];
            int currentByte = 0;

            // Empty the buffer
            int bytesToRead = Math.Min(_count - _index, count) + _index;
            for (int i = _index; i < bytesToRead; i++)
            {
                buffer[currentByte++] = _middleBuffer[i];
            }

            _index = _count;

            // Read from stream
            while (currentByte < count)
            {
                currentByte += await _underlyingStream.ReadAsync(buffer, currentByte, count - currentByte).ConfigureAwait(false);
            }

            //Debug.WriteLine("ReadBytes(" + count + ") : " + sw.ElapsedMilliseconds);

            return buffer;
        }
    }
}