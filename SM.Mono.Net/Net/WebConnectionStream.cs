//
// System.Net.WebConnectionStream
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
// (C) 2004 Novell, Inc (http://www.novell.com)
// Copyright (c) 2014 Henric Jungheim <software@henric.org>
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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SM.Mono.Net
{
    class WebConnectionStream : Stream
    {
        static readonly byte[] Crlf = { 13, 10 };
        readonly bool _allowBuffering;
        readonly AsyncCallback _cbWrapper; // Calls to ReadCallbackWrapper or WriteCallbacWrapper
        readonly WebConnection _cnc;
        readonly bool _isRead;
        readonly object _locker = new object();
        readonly ManualResetEvent _pending;
        readonly HttpWebRequest _request;
        readonly long _streamLength; // -1 when CL not present
        internal bool IgnoreIoErrors;
        internal long TotalWritten;
        bool _completeRequestWritten;
        long _contentLength;
        bool _disposed;
        byte[] _headers;
        bool _headersSent;
        bool _initRead;
        bool _nextReadCalled;
        int _pendingReads;
        int _pendingWrites;
        byte[] _readBuffer;
        int _readBufferOffset;
        int _readBufferSize;
        bool _readEof;
        int _readTimeout;
        bool _requestWritten;
        bool _sendChunked;
        long _totalRead;
        MemoryStream _writeBuffer;
        int _writeTimeout;
        static readonly byte[] ZeroChunk = Encoding.UTF8.GetBytes("0\r\n\r\n");

        public WebConnectionStream(WebConnection cnc, WebConnectionData data)
        {
            if (data == null)
                throw new InvalidOperationException("data was not initialized");
            if (data.Headers == null)
                throw new InvalidOperationException("data.Headers was not initialized");
            if (data.Request == null)
                throw new InvalidOperationException("data.request was not initialized");
            _isRead = true;
            _cbWrapper = ReadCallbackWrapper;
            _pending = new ManualResetEvent(true);
            _request = data.Request;
            _readTimeout = _request.ReadWriteTimeout;
            _writeTimeout = _readTimeout;
            _cnc = cnc;
            var contentType = data.Headers["Transfer-Encoding"];
            var chunkedRead = (contentType != null && contentType.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) != -1);
            var clength = data.Headers["Content-Length"];

            // Negative numbers?
            if (!long.TryParse(clength, out _streamLength))
                _streamLength = -1;

            if (!chunkedRead && _streamLength >= 0)
            {
                _contentLength = _streamLength;

                if (_contentLength == 0 && !IsNtlmAuth())
                    ReadAllAsync().Wait(); // TODO: Don't block.
            }
            else
                _contentLength = long.MaxValue;
        }

        public WebConnectionStream(WebConnection cnc, HttpWebRequest request)
        {
            _readTimeout = request.ReadWriteTimeout;
            _writeTimeout = _readTimeout;
            _isRead = false;
            _cbWrapper = WriteCallbackWrapper;
            _cnc = cnc;
            _request = request;
            _allowBuffering = request.InternalAllowBuffering;
            _sendChunked = request.SendChunked;
            if (_sendChunked)
                _pending = new ManualResetEvent(true);
            else if (_allowBuffering)
                _writeBuffer = new MemoryStream();
        }

        internal HttpWebRequest Request
        {
            get { return _request; }
        }

        internal WebConnection Connection
        {
            get { return _cnc; }
        }

        public override bool CanTimeout
        {
            get { return true; }
        }

        public override int ReadTimeout
        {
            get { return _readTimeout; }

            set
            {
                if (value < -1)
                    throw new ArgumentOutOfRangeException("value");
                _readTimeout = value;
            }
        }

        public override int WriteTimeout
        {
            get { return _writeTimeout; }

            set
            {
                if (value < -1)
                    throw new ArgumentOutOfRangeException("value");
                _writeTimeout = value;
            }
        }

        internal bool CompleteRequestWritten
        {
            get { return _completeRequestWritten; }
        }

        internal bool SendChunked
        {
            set { _sendChunked = value; }
        }

        internal byte[] ReadBuffer
        {
            set { _readBuffer = value; }
        }

        internal int ReadBufferOffset
        {
            set { _readBufferOffset = value; }
        }

        internal int ReadBufferSize
        {
            set { _readBufferSize = value; }
        }

        internal byte[] WriteBuffer
        {
            get { return _writeBuffer.GetBuffer(); }
        }

        internal int WriteBufferLength
        {
            get { return _writeBuffer != null ? (int)_writeBuffer.Length : -1; }
        }

        internal bool RequestWritten
        {
            get { return _requestWritten; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanRead
        {
            get { return !_disposed && _isRead; }
        }

        public override bool CanWrite
        {
            get { return !_disposed && !_isRead; }
        }

        public override long Length
        {
            get
            {
                if (!_isRead)
                    throw new NotSupportedException();
                return _streamLength;
            }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        bool CheckAuthHeader(string headerName)
        {
            var authHeader = _cnc.Data.Headers[headerName];
            return (authHeader != null && authHeader.IndexOf("NTLM", StringComparison.Ordinal) != -1);
        }

        bool IsNtlmAuth()
        {
            var isProxy = (_request.Proxy != null && !_request.Proxy.IsBypassed(_request.Address));
            if (isProxy && CheckAuthHeader("Proxy-Authenticate"))
                return true;
            return CheckAuthHeader("WWW-Authenticate");
        }

        internal async Task CheckResponseInBufferAsync()
        {
            if (_contentLength > 0 && (_readBufferSize - _readBufferOffset) >= _contentLength)
            {
                if (!IsNtlmAuth())
                    await ReadAllAsync().ConfigureAwait(false);
            }
        }

        internal void ForceCompletion()
        {
            if (!_nextReadCalled)
            {
                if (_contentLength == long.MaxValue)
                    _contentLength = 0;
                _nextReadCalled = true;
                _cnc.NextRead();
            }
        }

        internal void CheckComplete()
        {
            var nrc = _nextReadCalled;
            if (!nrc && _readBufferSize - _readBufferOffset == _contentLength)
            {
                _nextReadCalled = true;
                _cnc.NextRead();
            }
        }

        internal async Task ReadAllAsync()
        {
            if (!_isRead || _readEof || _totalRead >= _contentLength || _nextReadCalled)
            {
                if (_isRead && !_nextReadCalled)
                {
                    _nextReadCalled = true;
                    _cnc.NextRead();
                }

                return;
            }

            _pending.WaitOne();
            var isLocked = false;
            try
            {
                Monitor.TryEnter(_locker, ref isLocked);

                if (_totalRead >= _contentLength)
                    return;

                byte[] b = null;
                var diff = _readBufferSize - _readBufferOffset;
                int newSize;

                if (_contentLength == long.MaxValue)
                {
                    var ms = new MemoryStream();
                    byte[] buffer = null;
                    if (_readBuffer != null && diff > 0)
                    {
                        ms.Write(_readBuffer, _readBufferOffset, diff);
                        if (_readBufferSize >= 8192)
                            buffer = _readBuffer;
                    }

                    if (buffer == null)
                        buffer = new byte[8192];

                    int read;
                    while ((read = await _cnc.ReadAsync(_request, buffer, 0, buffer.Length, CancellationToken.None)) != 0)
                        ms.Write(buffer, 0, read);

                    b = ms.GetBuffer();
                    newSize = (int)ms.Length;
                    _contentLength = newSize;
                }
                else
                {
                    newSize = (int)(_contentLength - _totalRead);
                    b = new byte[newSize];
                    if (_readBuffer != null && diff > 0)
                    {
                        if (diff > newSize)
                            diff = newSize;

                        Buffer.BlockCopy(_readBuffer, _readBufferOffset, b, 0, diff);
                    }

                    var remaining = newSize - diff;
                    var r = -1;
                    while (remaining > 0 && r != 0)
                    {
                        r = await _cnc.ReadAsync(_request, b, diff, remaining, CancellationToken.None);
                        remaining -= r;
                        diff += r;
                    }
                }

                _readBuffer = b;
                _readBufferOffset = 0;
                _readBufferSize = newSize;
                _totalRead = 0;
                _nextReadCalled = true;
            }
            finally
            {
                if (isLocked)
                    Monitor.Exit(_locker);
            }

            _cnc.NextRead();
        }

        void WriteCallbackWrapper(IAsyncResult r)
        {
            var result = r as WebAsyncResult;
            if (result != null && result.AsyncWriteAll)
                return;

            if (r.AsyncState != null)
            {
                result = (WebAsyncResult)r.AsyncState;
                result.InnerAsyncResult = r;
                result.DoCallback();
            }
            else
            {
                try
                {
                    EndWrite(r);
                }
                catch
                { }
            }
        }

        void ReadCallbackWrapper(IAsyncResult r)
        {
            WebAsyncResult result;
            if (r.AsyncState != null)
            {
                result = (WebAsyncResult)r.AsyncState;
                result.InnerAsyncResult = r;
                result.DoCallback();
            }
            else
            {
                try
                {
                    EndRead(r);
                }
                catch
                { }
            }
        }

        public override int Read(byte[] buffer, int offset, int size)
        {
            var cb = _cbWrapper;
            var res = (WebAsyncResult)BeginRead(buffer, offset, size, cb, null);
            if (!res.IsCompleted && !res.WaitUntilComplete(ReadTimeout, false))
            {
                _nextReadCalled = true;
                _cnc.Close(true);
                throw new WebException("The operation has timed out.", WebExceptionStatus.Timeout);
            }

            return EndRead(res);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int size,
            AsyncCallback cb, object state)
        {
            var tcs = new TaskCompletionSource<int>(state);

            ReadAsync(buffer, offset, size)
                .ContinueWith(t =>
                              {
                                  if (t.IsFaulted)
                                      tcs.TrySetException(t.Exception);
                                  else if (t.IsCanceled)
                                      tcs.TrySetCanceled();
                                  else
                                      tcs.TrySetResult(t.Result);

                                  cb(t);
                              });

            return tcs.Task;
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            var task = (Task<int>)asyncResult;

            return task.Result;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            if (!_isRead)
                throw new NotSupportedException("this stream does not allow reading");

            if (buffer == null)
                throw new ArgumentNullException("buffer");

            var length = buffer.Length;
            if (offset < 0 || length < offset)
                throw new ArgumentOutOfRangeException("offset");
            if (size < 0 || (length - offset) < size)
                throw new ArgumentOutOfRangeException("size");

            //var result = new WebAsyncResult(cb, state, buffer, offset, size);
            if (_totalRead >= _contentLength)
            {
                return 0;
                //result.SetCompleted(true, -1);
                //result.DoCallback();
                //return result;
            }

            lock (_locker)
            {
                _pendingReads++;
                _pending.Reset();
            }

            var nbytes2 = 0;

            var remaining = _readBufferSize - _readBufferOffset;
            if (remaining > 0)
            {
                var copy = (remaining > size) ? size : remaining;
                Buffer.BlockCopy(_readBuffer, _readBufferOffset, buffer, offset, copy);
                _readBufferOffset += copy;
                offset += copy;
                size -= copy;
                _totalRead += copy;
                if (size == 0 || _totalRead >= _contentLength)
                {
                    return copy;
                    //result.SetCompleted(true, copy);
                    //result.DoCallback();
                    //return result;
                }

                nbytes2 = copy;
            }

            //if (cb != null)
            //    cb = _cbWrapper;

            if (_contentLength != long.MaxValue && _contentLength - _totalRead < size)
                size = (int)(_contentLength - _totalRead);

            if (_readEof)
            {
                return nbytes2;
                //result.SetCompleted(true, result.NBytes);
                //result.DoCallback();
            }

            int nbytes;

            try
            {
                nbytes = await _cnc.ReadAsync(_request, buffer, offset, size, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_locker)
                {
                    _pendingReads--;
                    if (_pendingReads == 0)
                        _pending.Set();
                }

                _nextReadCalled = true;
                _cnc.Close(true);

                throw;
            }

            if (nbytes < 0)
            {
                nbytes = 0;
                _readEof = true;
            }

            _totalRead += nbytes;

            if (0 == nbytes)
                _contentLength = _totalRead;
            //    return result;
            //}

            //public override int EndRead(IAsyncResult r)
            //{
            //    var result = (WebAsyncResult)r;
            //    if (result.EndCalled)
            //    {
            //        var xx = result.NBytes;
            //        return (xx >= 0) ? xx : 0;
            //    }

            //    result.EndCalled = true;

            //    if (!result.IsCompleted)
            //{
            //var nbytes = -1;
            //    try
            //    {
            //        nbytes = _cnc.EndRead(_request, result);
            //    }
            //    catch (Exception exc)
            //    {
            //        lock (_locker)
            //        {
            //            _pendingReads--;
            //            if (_pendingReads == 0)
            //                _pending.Set();
            //        }

            //        _nextReadCalled = true;
            //        _cnc.Close(true);
            //        result.SetCompleted(false, exc);
            //        result.DoCallback();
            //        throw;
            //    }

            //    if (nbytes < 0)
            //    {
            //        nbytes = 0;
            //        _readEof = true;
            //    }

            //    _totalRead += nbytes;
            //    result.SetCompleted(false, nbytes + result.NBytes);
            //    result.DoCallback();
            //    if (nbytes == 0)
            //        _contentLength = _totalRead;
            //}

            lock (_locker)
            {
                _pendingReads--;
                if (_pendingReads == 0)
                    _pending.Set();
            }

            if (_totalRead >= _contentLength && !_nextReadCalled)
                await ReadAllAsync().ConfigureAwait(false);

            var nb = nbytes + nbytes2;

            return (nb >= 0) ? nb : 0;
        }

        //void WriteAsyncCB(IAsyncResult r)
        //{
        //    var result = (WebAsyncResult)r.AsyncState;
        //    result.InnerAsyncResult = null;

        //    try
        //    {
        //        _cnc.EndWrite(_request, true, r);
        //        result.SetCompleted(false, 0);
        //        if (!_initRead)
        //        {
        //            _initRead = true;
        //            WebConnection.StartReading(_cnc);
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        KillBuffer();
        //        _nextReadCalled = true;
        //        _cnc.Close(true);
        //        if (e is SocketException)
        //            e = new IOException("Error writing request", e);
        //        result.SetCompleted(false, e);
        //    }

        //    if (_allowBuffering && !_sendChunked && _request.ContentLength > 0 && TotalWritten == _request.ContentLength)
        //        _completeRequestWritten = true;

        //    result.DoCallback();
        //}

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int size,
            AsyncCallback cb, object state)
        {
            var tcs = new TaskCompletionSource<object>();

            WriteAsync(buffer, offset, size, CancellationToken.None)
                .ContinueWith(t =>
                              {
                                  if (t.IsFaulted)
                                      tcs.TrySetException(t.Exception);
                                  else if (t.IsCanceled)
                                      tcs.TrySetCanceled();
                                  else
                                      tcs.TrySetResult(string.Empty);

                                  cb(t);
                              });

            return tcs.Task;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            if (_request.Aborted)
                throw new WebException("The request was canceled.", WebExceptionStatus.RequestCanceled);

            if (_isRead)
                throw new NotSupportedException("this stream does not allow writing");

            if (buffer == null)
                throw new ArgumentNullException("buffer");

            var length = buffer.Length;
            if (offset < 0 || length < offset)
                throw new ArgumentOutOfRangeException("offset");
            if (size < 0 || (length - offset) < size)
                throw new ArgumentOutOfRangeException("size");

            if (_sendChunked)
            {
                lock (_locker)
                {
                    _pendingWrites++;
                    _pending.Reset();
                }
            }

            //var result = new WebAsyncResult(cb, state);
            //AsyncCallback callback = WriteAsyncCB;

            if (_sendChunked)
            {
                _requestWritten = true;

                var cSize = String.Format("{0:X}\r\n", size);
                var head = Encoding.UTF8.GetBytes(cSize);
                var chunkSize = 2 + size + head.Length;
                var newBuffer = new byte[chunkSize];
                Buffer.BlockCopy(head, 0, newBuffer, 0, head.Length);
                Buffer.BlockCopy(buffer, offset, newBuffer, head.Length, size);
                Buffer.BlockCopy(Crlf, 0, newBuffer, head.Length + size, Crlf.Length);

                buffer = newBuffer;
                offset = 0;
                size = chunkSize;
            }
            else
            {
                CheckWriteOverflow(_request.ContentLength, TotalWritten, size);

                if (_allowBuffering)
                {
                    if (_writeBuffer == null)
                        _writeBuffer = new MemoryStream();
                    _writeBuffer.Write(buffer, offset, size);
                    TotalWritten += size;

                    if (_request.ContentLength <= 0 || TotalWritten < _request.ContentLength)
                    {
                        return;
                        //result.SetCompleted(true, 0);
                        //result.DoCallback();
                        //return result;
                    }

                    //result.AsyncWriteAll = true;
                    _requestWritten = true;
                    buffer = _writeBuffer.GetBuffer();
                    offset = 0;
                    size = (int)TotalWritten;
                }
            }

            try
            {
                await _cnc.WriteAsync(_request, buffer, offset, size, cancellationToken).ConfigureAwait(false);
                //result.InnerAsyncResult = _cnc.BeginWrite(_request, buffer, offset, size, callback, result);
                //if (result.InnerAsyncResult == null)
                //{
                //    if (!result.IsCompleted)
                //        result.SetCompleted(true, 0);
                //    result.DoCallback();
                //}

                if (!_initRead)
                {
                    _initRead = true;
                    WebConnection.InitRead(_cnc);
                }
            }
            catch (Exception e)
            {
                KillBuffer();
                _nextReadCalled = true;
                _cnc.Close(true);
                if (e is SocketException)
                    throw new IOException("Error writing request", e);

                throw;
                //if (!IgnoreIoErrors)
                //    throw;
                //result.SetCompleted(true, 0);
                //result.DoCallback();
            }

            TotalWritten += size;

            if (_allowBuffering && !_sendChunked && _request.ContentLength > 0 && TotalWritten == _request.ContentLength)
                _completeRequestWritten = true;

            if (_allowBuffering && !_sendChunked)
                return;

            if (_sendChunked)
            {
                lock (_locker)
                {
                    _pendingWrites--;
                    if (_pendingWrites == 0)
                        _pending.Set();
                }
            }
        }

        void CheckWriteOverflow(long contentLength, long totalWritten, long size)
        {
            if (contentLength == -1)
                return;

            var avail = contentLength - totalWritten;
            if (size > avail)
            {
                KillBuffer();
                _nextReadCalled = true;
                _cnc.Close(true);
                throw new ProtocolViolationException(
                    "The number of bytes to be written is greater than " +
                    "the specified ContentLength.");
            }
        }

        public override void EndWrite(IAsyncResult r)
        {
            var task = (Task)r;

            task.Wait();
            //if (r == null)
            //    throw new ArgumentNullException("r");

            //var result = r as WebAsyncResult;
            //if (result == null)
            //    throw new ArgumentException("Invalid IAsyncResult");

            //if (result.EndCalled)
            //    return;

            //result.EndCalled = true;
            //if (result.AsyncWriteAll)
            //{
            //    result.WaitUntilComplete();
            //    if (result.GotException)
            //        throw result.Exception;
            //    return;
            //}

            //if (_allowBuffering && !_sendChunked)
            //    return;

            //if (result.GotException)
            //    throw result.Exception;

            //if (_sendChunked)
            //{
            //    lock (_locker)
            //    {
            //        _pendingWrites--;
            //        if (_pendingWrites == 0)
            //            _pending.Set();
            //    }
            //}
        }

        public override void Write(byte[] buffer, int offset, int size)
        {
            var cb = _cbWrapper;
            var res = (WebAsyncResult)BeginWrite(buffer, offset, size, cb, null);
            if (!res.IsCompleted && !res.WaitUntilComplete(WriteTimeout, false))
            {
                KillBuffer();
                _nextReadCalled = true;
                _cnc.Close(true);
                throw new IOException("Write timed out.");
            }

            EndWrite(res);
        }

        public override void Flush()
        { }

        internal async Task SetHeadersAsync(bool setInternalLength)
        {
            if (_headersSent)
                return;

            var method = _request.Method;
            var noWritestream = (method == "GET" || method == "CONNECT" || method == "HEAD" ||
                                 method == "TRACE");
            var webdav = (method == "PROPFIND" || method == "PROPPATCH" || method == "MKCOL" ||
                          method == "COPY" || method == "MOVE" || method == "LOCK" ||
                          method == "UNLOCK");

            if (setInternalLength && !noWritestream && _writeBuffer != null)
                _request.InternalContentLength = _writeBuffer.Length;

            if (_sendChunked || _request.ContentLength > -1 || noWritestream || webdav)
            {
                _headersSent = true;
                _headers = _request.GetRequestHeaders();

                try
                {
                    await _cnc.WriteAsync(_request, _headers, 0, _headers.Length, CancellationToken.None).ConfigureAwait(false);

                    var flushTask = _cnc.FlushAsync(_request, CancellationToken.None);

                    if (!_initRead)
                    {
                        _initRead = true;
                        WebConnection.InitRead(_cnc);
                    }

                    await flushTask.ConfigureAwait(false);

                    var cl = _request.ContentLength;

                    if (!_sendChunked && cl == 0)
                        _requestWritten = true;
                }
                catch (WebException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new WebException("Error writing headers", e, WebExceptionStatus.SendFailure, null);
                }

                //var result = new WebAsyncResult(callback, state);
                //result.InnerAsyncResult = _cnc.BeginWrite(_request, _headers, 0, _headers.Length, SetHeadersCB, result);
                //if (result.InnerAsyncResult != null)
                //    return result;
            }
        }

        //void SetHeadersCB(IAsyncResult r)
        //{
        //    var result = (WebAsyncResult)r.AsyncState;
        //    result.InnerAsyncResult = null;
        //    try
        //    {
        //        _cnc.EndWrite(_request, true, r);
        //        result.SetCompleted(false, 0);
        //        if (!_initRead)
        //        {
        //            _initRead = true;
        //            WebConnection.ReadAsync(_cnc);
        //        }
        //        var cl = _request.ContentLength;
        //        if (!_sendChunked && cl == 0)
        //            _requestWritten = true;
        //    }
        //    catch (WebException e)
        //    {
        //        result.SetCompleted(false, e);
        //    }
        //    catch (Exception e)
        //    {
        //        result.SetCompleted(false, new WebException("Error writing headers", e, WebExceptionStatus.SendFailure, null));
        //    }
        //    result.DoCallback();
        //}

        internal async Task WriteRequestAsync()
        {
            if (_requestWritten)
                return;

            _requestWritten = true;
            if (_sendChunked)
                return;

            if (!_allowBuffering || _writeBuffer == null)
                return;

            //var bytes = _writeBuffer.GetBuffer();
            var length = (int)_writeBuffer.Length;
            if (_request.ContentLength != -1 && _request.ContentLength < length)
            {
                _nextReadCalled = true;
                _cnc.Close(true);
                throw new WebException("Specified Content-Length is less than the number of bytes to write", null,
                    WebExceptionStatus.ServerProtocolViolation, null);
            }

            await SetHeadersAsync(true).ConfigureAwait(false);

            //var result = new WebAsyncResult(callback, state);
            //result.InnerAsyncResult = SetHeadersAsync(true, WriteRequestAsyncCB, result);
            //if (result.InnerAsyncResult == null)
            //    WriteRequestAsyncCB(result);
            //return result;
            //}

            //void WriteRequestAsyncCB(IAsyncResult ar)
            //{
            //    var result = (WebAsyncResult)ar;
            //    var innerResult = (WebAsyncResult)result.InnerAsyncResult;
            //    result.InnerAsyncResult = null;

            //    if (innerResult != null && innerResult.GotException)
            //    {
            //        result.SetCompleted(false, innerResult.Exception);
            //        result.DoCallback();
            //        return;
            //    }

            if (_cnc.Data.StatusCode != 0 && _cnc.Data.StatusCode != 100)
            {
                //result.SetCompleted(false, 0);
                //result.DoCallback();
                return;
            }

            var bytes = _writeBuffer.GetBuffer();
            length = (int)_writeBuffer.Length;

            if (length > 0)
            {
                _completeRequestWritten = await _cnc.WriteAsync(_request, bytes, 0, length, CancellationToken.None);
                //result.InnerAsyncResult = _cnc.BeginWrite(_request, bytes, 0, length, WriteRequestAsyncCB2, result);
            }

            if (!_initRead)
            {
                _initRead = true;
                WebConnection.InitRead(_cnc);

                return;
            }

            if (0 == length)
            {
                //result.SetCompleted(false, 0);
                //result.DoCallback();
                _completeRequestWritten = true;
            }
        }

        //void WriteRequestAsyncCB2(IAsyncResult ar)
        //{
        //    var result = (WebAsyncResult)ar.AsyncState;
        //    var innerResult = result.InnerAsyncResult;
        //    result.InnerAsyncResult = null;

        //    try
        //    {
        //        _completeRequestWritten = _cnc.EndWrite(_request, false, innerResult);
        //        result.SetCompleted(false, 0);
        //    }
        //    catch (Exception exc)
        //    {
        //        result.SetCompleted(false, exc);
        //    }
        //    finally
        //    {
        //        result.DoCallback();
        //    }
        //}

        internal void InternalClose()
        {
            _disposed = true;
        }

        /// <summary>
        ///     Warning: Sync over async.
        /// </summary>
        public override void Close()
        {
            CloseAsync().Wait();
        }

        public async Task CloseAsync()
        {
            if (_sendChunked)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _pending.WaitOne();

                await _cnc.WriteAsync(_request, ZeroChunk, 0, ZeroChunk.Length, CancellationToken.None).ConfigureAwait(false);
                
                return;
            }

            if (_isRead)
            {
                if (!_nextReadCalled)
                {
                    CheckComplete();
                    // If we have not read all the contents
                    if (!_nextReadCalled)
                    {
                        _nextReadCalled = true;
                        _cnc.Close(true);
                    }
                }

                return;
            }

            if (!_allowBuffering)
            {
                _completeRequestWritten = true;

                if (!_initRead)
                {
                    _initRead = true;
                    WebConnection.InitRead(_cnc);
                }

                return;
            }

            if (_disposed || _requestWritten)
                return;

            var length = _request.ContentLength;

            if (!_sendChunked && length != -1 && TotalWritten != length)
            {
                var io = new IOException("Cannot close the stream until all bytes are written");
                _nextReadCalled = true;
                _cnc.Close(true);
                throw new WebException("Request was cancelled.", io, WebExceptionStatus.RequestCanceled, null);
            }

            // Commented out the next line to fix xamarin bug #1512
            //WriteRequest ();
            _disposed = true;
        }

        internal void KillBuffer()
        {
            _writeBuffer = null;
        }

        public override long Seek(long a, SeekOrigin b)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long a)
        {
            throw new NotSupportedException();
        }
    }
}
