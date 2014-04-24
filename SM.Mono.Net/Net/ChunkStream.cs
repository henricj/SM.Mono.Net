//
// System.Net.ChunkStream
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

#if SILVERLIGHT 
using SM.Mono.Text;
#endif

namespace SM.Mono.Net
{
    class ChunkStream
    {
#if SILVERLIGHT
        static readonly Encoding HttpEncoding = new Latin1Encoding();
#else
        static readonly Encoding HttpEncoding = Encoding.GetEncoding("iso-8859-1");
#endif
        readonly MemoryStream _buffer = new MemoryStream();
        readonly WebHeaderCollection _headers;
        readonly MemoryStream _saved = new MemoryStream();
        int _chunkRead;
        int _chunkSize = -1;
        bool _gotit;
        bool _sawCr;
        State _state;
        int _totalWritten;
        int _trailerState;
        static readonly byte[] StBytes = { (byte)'\r', (byte)'\n', (byte)'\r' };

        public ChunkStream(byte[] buffer, int offset, int size, WebHeaderCollection headers)
            : this(headers)
        {
            Write(buffer, offset, size);
        }

        public ChunkStream(WebHeaderCollection headers)
        {
            _headers = headers;
            _totalWritten = 0;
        }

        public bool WantMore
        {
            get { return _chunkRead != _chunkSize || _chunkSize != 0 || (_state != State.None && _state != State.End); }
        }

        public bool DataAvailable
        {
            get { return _buffer.Position < _buffer.Length; }
        }

        public int TotalDataSize
        {
            get { return _totalWritten; }
        }

        public int ChunkLeft
        {
            get { return _chunkSize - _chunkRead; }
        }

        public void ResetBuffer()
        {
            _chunkSize = -1;
            _chunkRead = 0;
            _totalWritten = 0;
            _buffer.SetLength(0);
        }

        public void WriteAndReadBack(byte[] buffer, int offset, int size, ref int read)
        {
            if (offset + read > 0)
                Write(buffer, offset, offset + read);

            read = Read(buffer, offset, size);
        }

        public int Read(byte[] buffer, int offset, int size)
        {
            return _buffer.Read(buffer, offset, size);
        }

        public void Write(byte[] buffer, int begin, int end)
        {
            if (begin < end)
                InternalWrite(buffer, begin, end);
        }

        void InternalWrite(byte[] buffer, int begin, int end)
        {
            do
            {
                if (_state == State.End)
                    return;

                if (_state == State.None)
                {
                    _state = GetChunkSize(buffer, ref begin, end);
                    if (_state == State.None)
                        return;

                    _saved.SetLength(0);
                    _sawCr = false;
                    _gotit = false;
                }

                if (_state == State.Body && begin < end)
                {
                    _state = ReadBody(buffer, ref begin, end);
                    if (_state == State.Body)
                        return;
                }

                if (_state == State.BodyFinished && begin < end)
                {
                    _state = ReadCRLF(buffer, ref begin, end);
                    if (_state == State.BodyFinished)
                        return;

                    _sawCr = false;
                }

                if (_state == State.Trailer && begin < end)
                {
                    _state = ReadTrailer(buffer, ref begin, end);
                    if (_state == State.Trailer)
                        return;

                    _saved.SetLength(0);
                    _sawCr = false;
                    _gotit = false;
                }
            } while (begin < end);
        }

        State ReadBody(byte[] buffer, ref int begin, int end)
        {
            if (_chunkSize == 0)
                return State.BodyFinished;

            var length = end - begin;
            if (length + _chunkRead > _chunkSize)
                length = _chunkSize - _chunkRead;

            AppendBuffer(buffer, begin, length);

            begin += length;
            _chunkRead += length;
            _totalWritten += length;

            return (_chunkRead == _chunkSize) ? State.BodyFinished : State.Body;
        }

        void AppendBuffer(byte[] buffer, int offset, int count)
        {
            if (null == buffer)
                throw new ArgumentNullException("buffer");
            if (offset < 0 || offset >= buffer.Length)
                throw new ArgumentOutOfRangeException("offset");
            if (count <= 0 || count + offset > buffer.Length)
                throw new ArgumentOutOfRangeException("count");

            var position = _buffer.Position;

            if (position > _buffer.Capacity / 4)
            {
                var p = _buffer.GetBuffer();

                var newLength = _buffer.Length - position;

                if (newLength > 0)
                    Array.Copy(p, (int)position, p, 0, (int)newLength);

                _buffer.Position = 0;
                _buffer.SetLength(newLength);

                position = 0;
            }

            _buffer.Seek(0, SeekOrigin.End);

            _buffer.Write(buffer, offset, count);

            _buffer.Position = position;
        }

        State GetChunkSize(byte[] buffer, ref int offset, int size)
        {
            _chunkRead = 0;
            _chunkSize = 0;
            var c = '\0';
            while (offset < size)
            {
                var b = buffer[offset++];
                c = (char)b;
                if (c == '\r')
                {
                    if (_sawCr)
                        ThrowProtocolViolation("2 CR found");

                    _sawCr = true;
                    continue;
                }

                if (_sawCr && c == '\n')
                    break;

                if (c == ' ')
                    _gotit = true;

                if (!_gotit)
                    _saved.WriteByte(b);

                if (_saved.Length > 20)
                    ThrowProtocolViolation("chunk size too long.");
            }

            if (!_sawCr || c != '\n')
            {
                if (offset < size)
                    ThrowProtocolViolation("Missing \\n");

                if (_saved.Length > 0)
                    _chunkSize = (int)ParseChunkSize();

                return State.None;
            }

            _chunkRead = 0;
            _chunkSize = (int)ParseChunkSize();

            if (_chunkSize == 0)
            {
                _trailerState = 2;

                return State.Trailer;
            }

            return State.Body;
        }

        long ParseChunkSize()
        {
            var chunk = HttpEncoding.GetString(_saved.GetBuffer(), 0, (int)_saved.Length);

            var chunkSize = RemoveChunkExtension(chunk);

            long size;
            if (!long.TryParse(chunkSize, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size))
                ThrowProtocolViolation("Cannot parse chunk size.");

            //if (size > ???)
            //    throw new WebException("Chunk size too large", null, WebExceptionStatus.UnknownError, null);

            return size;
        }

        static string RemoveChunkExtension(string input)
        {
            var idx = input.IndexOf(';');
            if (idx == -1)
                return input;
            return input.Substring(0, idx);
        }

        State ReadCRLF(byte[] buffer, ref int offset, int size)
        {
            if (!_sawCr)
            {
                if ((char)buffer[offset++] != '\r')
                    ThrowProtocolViolation("Expecting \\r");

                _sawCr = true;
                if (offset == size)
                    return State.BodyFinished;
            }

            if (_sawCr && (char)buffer[offset++] != '\n')
                ThrowProtocolViolation("Expecting \\n");

            return State.None;
        }

        State ReadTrailer(byte[] buffer, ref int offset, int size)
        {
            // short path
            if (_trailerState == 2 && (char)buffer[offset] == '\r' && _saved.Length == 0)
            {
                offset++;
                if (offset < size && (char)buffer[offset] == '\n')
                {
                    offset++;
                    return State.End;
                }
                offset--;
            }

            var st = _trailerState;

            while (offset < size && st < 4)
            {
                var c = (char)buffer[offset++];

                if ((st == 0 || st == 2) && c == '\r')
                {
                    st++;
                    continue;
                }

                if ((st == 1 || st == 3) && c == '\n')
                {
                    st++;
                    continue;
                }

                if (st > 0)
                {
                    _saved.Write(StBytes, 0, _saved.Length == 0 ? st - 2 : st);
                    st = 0;
                    if (_saved.Length > 4196)
                        ThrowProtocolViolation("Error reading trailer (too long).");
                }
            }

            if (st < 4)
            {
                _trailerState = st;
                if (offset < size)
                    ThrowProtocolViolation("Error reading trailer.");

                return State.Trailer;
            }

            using (var reader = new StreamReader(_saved, HttpEncoding, false, 1024, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null && line != "")
                {
                    _headers.Add(line);
                }
            }

            return State.End;
        }

        static void ThrowProtocolViolation(string message)
        {
            var we = new WebException(message, null, WebExceptionStatus.ServerProtocolViolation, null);
            throw we;
        }

        #region Nested type: State

        enum State
        {
            None,
            Body,
            BodyFinished,
            Trailer,
            End
        }

        #endregion
    }
}
