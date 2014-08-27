// Copyright (c) 2014 Henric Jungheim <software@henric.org>
// 
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SM.Mono.Net.Sockets;
using SM.Mono.Text;

namespace SM.Mono.Utility
{
    sealed class HttpLineReader : IDisposable
    {
        const int InitialCapacity = 1024;
        const int MaximumCapacity = 8192;
        const int ResizeRead = 1024;
        const int MinimumRead = 64;
        const int MaximumRead = 1024;

        readonly Encoding _encoding;
        readonly StreamSocketStream _stream;
        bool _badLine;
        int _begin;
        byte[] _buffer = new byte[InitialCapacity];
        int _end;

        public HttpLineReader(StreamSocketStream stream, Encoding encoding = null)
        {
            if (null == stream)
                throw new ArgumentNullException("stream");

            _stream = stream;
            _encoding = encoding ?? new Latin1Encoding();
        }

        #region IDisposable Members

        public void Dispose()
        {
            _buffer = null;
        }

        #endregion

        public void Clear()
        {
            _begin = 0;
            _end = 0;
        }

        public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            _badLine = false;

            for (; ; )
            {
                var eolIndex = FindLine();

                if (eolIndex >= 0)
                {
                    var begin = _begin;

                    _begin = eolIndex;

                    if (_badLine)
                        _badLine = false;
                    else
                        return CreateString(begin, eolIndex);
                }

                var remaining = _buffer.Length - _end;

                if (_begin > 0)
                {
                    if (remaining < 128 || _begin > _buffer.Length - 128)
                    {
                        var size = _end - _begin;

                        Array.Copy(_buffer, _begin, _buffer, 0, size);
                        _begin = 0;
                        _end -= size;

                        remaining = _buffer.Length - _end;
                    }
                }

                if (remaining < ResizeRead)
                {
                    if (_buffer.Length < MaximumCapacity)
                    {
                        var newBuffer = new byte[Math.Min(MaximumCapacity, 2 * _buffer.Length)];

                        if (_end > _begin)
                        {
                            Array.Copy(_buffer, _begin, newBuffer, 0, _end - _begin);
                            _end = _end - _begin;
                            _begin = 0;
                        }
                        else
                        {
                            _begin = _end = 0;
                        }

                        _buffer = newBuffer;

                        remaining = _buffer.Length - _end;

                        if (remaining < MinimumRead)
                        {
                            // Throw it away.  What should we do with huge lines?   
                            Clear();

                            _badLine = true;

                            remaining = _buffer.Length - _end;
                        }
                    }
                }

                var readLength = Math.Min(remaining, MaximumRead);

                var length = await _stream.ReadAsync(_buffer, _end, readLength, cancellationToken).ConfigureAwait(false);

                if (length < 1)
                {
                    if (_badLine)
                    {
                        Clear();

                        return null;
                    }

                    return CreateString(_begin, _end);
                }

                _end += length;
            }
        }

        int FindLine()
        {
            for (var i = _begin; i < _end; ++i)
            {
                var ch = _buffer[i];

                if ('\n' == ch)
                    return i + 1;

                if ('\r' == ch)
                {
                    if (i + 1 < _end)
                    {
                        if ('\n' == _buffer[i + 1])
                            return i + 2;

                        return i + 1;
                    }
                }
            }

            return -1;
        }

        public void SyncStream()
        {
            var size = _end - _begin;

            if (size > 0)
                _stream.Unread(_buffer, _begin, size);

            Clear();
        }

        string CreateString(int begin, int end)
        {
            var length = end - begin;

            if (length < 1)
                return null;

            var lastCh = (char)_buffer[end - 1];

            switch (lastCh)
            {
                case '\n':
                    --end;
                    if (end > begin && '\r' == (char)_buffer[end - 1])
                        --end;
                    break;
                case '\r':
                    --end;
                    break;
                default:
                    return null;
            }

            var line = end > begin ? _encoding.GetString(_buffer, begin, end - begin) : string.Empty;

            return line;
        }
    }
}
