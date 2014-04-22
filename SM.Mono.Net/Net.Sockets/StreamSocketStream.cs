//
// System.Net.Sockets.NetworkStream.cs
//
// Author:
//   Miguel de Icaza (miguel@ximian.com)
//   Sridhar Kulkarni <sridharkulkarni@gmail.com>
//
// (C) 2002 Ximian, Inc. http://www.ximian.com
// Copyright (C) 2002-2006 Novell, Inc.  http://www.novell.com
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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
#if !NET_2_1 || MOBILE
//using System.Timers;
using System.Threading;

#endif

namespace SM.Mono.Net.Sockets
{
    public class StreamSocketStream : Stream
    {
        FileAccess _access;
        StreamSocket _socket;
        Stream _inputStream;
        readonly bool _ownsSocket;
        bool _readable, _writeable;
        bool _disposed;
        Stream _outputStream;
        MemoryStream _unreadData;
        bool _unreadRead;

        public StreamSocketStream(StreamSocket socket, bool ownsSocket)
            : this(socket, FileAccess.ReadWrite, ownsSocket)
        { }

        public StreamSocketStream(StreamSocket socket, FileAccess access = FileAccess.ReadWrite, bool ownsSocket = false)
        {
            if (socket == null)
                throw new ArgumentNullException("socket");
            //if (socket.SocketType != SocketType.Stream)
            //    throw new ArgumentException ("Socket is not of type Stream", "socket");
            //if (!socket.Connected)
            //    throw new IOException("Not connected");
            //if (!socket.Blocking)
            //    throw new IOException ("Operation not allowed on a non-blocking socket.");

            _socket = socket;
            _ownsSocket = ownsSocket;
            _access = access;

            _readable = CanRead;
            _writeable = CanWrite;
        }

        public override bool CanRead
        {
            get { return _access == FileAccess.ReadWrite || _access == FileAccess.Read; }
        }

        public override bool CanSeek
        {
            get
            {
                // network sockets cant seek.
                return false;
            }
        }

        public override bool CanTimeout
        {
            get { return (true); }
        }

        public override bool CanWrite
        {
            get { return _access == FileAccess.ReadWrite || _access == FileAccess.Write; }
        }

        //public virtual bool DataAvailable {
        //    get {
        //        CheckDisposed ();
        //        return socket.Available > 0;
        //    }
        //}

        public override long Length
        {
            get
            {
                // Network sockets always throw an exception
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get
            {
                // Network sockets always throw an exception
                throw new NotSupportedException();
            }

            set
            {
                // Network sockets always throw an exception
                throw new NotSupportedException();
            }
        }

        protected bool Readable
        {
            get { return _readable; }

            set { _readable = value; }
        }

#if !NET_2_1 || MOBILE
#if TARGET_JVM
        [MonoNotSupported ("Not supported since Socket.ReceiveTimeout is not supported")]
#endif

        public override int ReadTimeout
        {
            get
            {
                var r = ReceiveTimeout;
                return (r <= 0) ? Timeout.Infinite : r;
            }
            set
            {
                if (value <= 0 && value != Timeout.Infinite)
                    throw new ArgumentOutOfRangeException("value", "The value specified is less than or equal to zero and is not Infinite.");

                ReceiveTimeout = value;
            }
        }

        int ReceiveTimeout { get; set; }
#endif

        protected Stream InputStream
        {
            get
            {
                if (null != _inputStream)
                    return _inputStream;

                var s = _socket;

                if (s == null)
                    throw new IOException("Connection closed");

                _inputStream = s.InputStream.AsStreamForRead();

                return _inputStream;
            }
        }

        protected Stream OutputStream
        {
            get
            {
                if (null != _outputStream)
                    return _outputStream;

                var s = _socket;

                if (s == null)
                    throw new IOException("Connection closed");

                _outputStream = s.OutputStream.AsStreamForWrite();

                return _outputStream;
            }
        }

        protected StreamSocket Socket
        {
            get { return _socket; }
        }

        protected bool Writeable
        {
            get { return _writeable; }

            set { _writeable = value; }
        }

#if !NET_2_1 || MOBILE
#if TARGET_JVM
        [MonoNotSupported ("Not supported since Socket.SendTimeout is not supported")]
#endif

        public override int WriteTimeout
        {
            get
            {
                var r = SendTimeout;
                return (r <= 0) ? Timeout.Infinite : r;
            }
            set
            {
                if (value <= 0 && value != Timeout.Infinite)
                    throw new ArgumentOutOfRangeException("value", "The value specified is less than or equal to zero and is not Infinite");

                SendTimeout = value;
            }
        }

        int SendTimeout { get; set; }
#endif

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckDisposed();

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            var len = buffer.Length;
            if (offset < 0 || offset > len)
                throw new ArgumentOutOfRangeException("offset", "offset exceeds the size of buffer");
            if (count <= 0 || offset + count > len)
                throw new ArgumentOutOfRangeException("count", "offset+size exceeds the size of buffer");

            if (null != _unreadData && _unreadData.Length > 0)
            {
                if (_unreadData.Position >= _unreadData.Length)
                    AdjustBuffer();
                else
                    return _unreadData.ReadAsync(buffer, offset, count, cancellationToken);
            }

            return InputStream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckDisposed();

            if (buffer == null)
                throw new ArgumentNullException("buffer");

            var len = buffer.Length;
            if (offset < 0 || offset > len)
                throw new ArgumentOutOfRangeException("offset", "offset exceeds the size of buffer");
            if (count <= 0 || offset + count > len)
                throw new ArgumentOutOfRangeException("count", "offset+size exceeds the size of buffer");

            return OutputStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int size,
            AsyncCallback callback, object state)
        {
            CheckDisposed();

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            var len = buffer.Length;
            if (offset < 0 || offset > len)
                throw new ArgumentOutOfRangeException("offset", "offset exceeds the size of buffer");
            if (size < 0 || offset + size > len)
                throw new ArgumentOutOfRangeException("size", "offset+size exceeds the size of buffer");

            if (null != _unreadData && _unreadData.Length > 0)
            {
                _unreadRead = true;

                if (_unreadData.Position >= _unreadData.Length)
                    AdjustBuffer();
                else
                    return _unreadData.BeginRead(buffer, offset, size, callback, state);
            }

            _unreadRead = false;

            return InputStream.BeginRead(buffer, offset, size, callback, state);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int size,
            AsyncCallback callback, object state)
        {
            CheckDisposed();

            if (buffer == null)
                throw new ArgumentNullException("buffer");

            var len = buffer.Length;
            if (offset < 0 || offset > len)
                throw new ArgumentOutOfRangeException("offset", "offset exceeds the size of buffer");
            if (size < 0 || offset + size > len)
                throw new ArgumentOutOfRangeException("size", "offset+size exceeds the size of buffer");

            return OutputStream.BeginWrite(buffer, offset, size, callback, state);
        }

#if !NET_2_1 || MOBILE
        public void Close(int timeout)
        {
            if (timeout < -1)
                throw new ArgumentOutOfRangeException("timeout", "timeout is less than -1");

            /* NB timeout is in milliseconds here, cf
             * seconds in Socket.Close(int)
             */
            Timer closeTimer = null;

            closeTimer = new Timer(obj =>
                                   {
                                       Close();

                                       // ReSharper disable once AccessToModifiedClosure
                                       var timer = closeTimer;

                                       if (null != timer)
                                           timer.Dispose();
                                   }, null, Timeout.Infinite, Timeout.Infinite);
        }
#endif

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (null != _inputStream)
            {
                _inputStream.Dispose();
                _inputStream = null;
            }

            if (null != _outputStream)
            {
                _outputStream.Dispose();
                _outputStream = null;
            }

            if (_ownsSocket)
            {
                var s = _socket;
                if (s != null)
                    s.Dispose();
            }
            _socket = null;
            _access = 0;
        }

        public override int EndRead(IAsyncResult ar)
        {
            CheckDisposed();

            if (ar == null)
                throw new ArgumentNullException("ar");

            var s = _unreadRead ? _unreadData : _inputStream;

            if (s == null)
                throw new IOException("Connection closed");

            return s.EndRead(ar);
        }

        public override void EndWrite(IAsyncResult ar)
        {
            CheckDisposed();
            if (ar == null)
                throw new ArgumentNullException("ar");

            var s = _outputStream;

            if (s == null)
                throw new IOException("Connection closed");

            s.EndWrite(ar);
        }

        public override void Flush()
        {
            // network streams are non-buffered, this is a no-op

            if (null != _inputStream)
                _inputStream.Flush();

            if (null != _outputStream)
                _outputStream.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            if (null != _inputStream)
                tasks.Add(_inputStream.FlushAsync(cancellationToken));

            if (null != _outputStream)
                tasks.Add(_outputStream.FlushAsync(cancellationToken));

            return Task.WhenAll(tasks);
        }

        public override int Read([In, Out] byte[] buffer, int offset, int size)
        {
            CheckDisposed();

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (offset < 0 || offset > buffer.Length)
                throw new ArgumentOutOfRangeException("offset", "offset exceeds the size of buffer");
            if (size < 0 || offset + size > buffer.Length)
                throw new ArgumentOutOfRangeException("size", "offset+size exceeds the size of buffer");

            if (null != _unreadData && _unreadData.Length > 0)
            {
                if (_unreadData.Position >= _unreadData.Length)
                    AdjustBuffer();
                else
                    return _unreadData.Read(buffer, offset, size);
            }

            return InputStream.Read(buffer, offset, size);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            // NetworkStream objects do not support seeking.

            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            // NetworkStream objects do not support SetLength

            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int size)
        {
            CheckDisposed();

            if (buffer == null)
                throw new ArgumentNullException("buffer");

            var len = buffer.Length;
            if (offset < 0 || offset > len)
                throw new ArgumentOutOfRangeException("offset", "offset exceeds the size of buffer");
            if (size < 0 || offset + size > len)
                throw new ArgumentOutOfRangeException("size", "offset+size exceeds the size of buffer");

            OutputStream.Write(buffer, offset, size);
        }

        void CheckDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        public Task UpgradeToSslAsync(HostName validationHostName, CancellationToken cancellationToken)
        {
            return _socket.UpgradeToSslAsync(SocketProtectionLevel.Ssl, validationHostName).AsTask(cancellationToken);
        }

#if TARGET_JVM
        public void ChangeToSSLSocket()
        {
            socket.ChangeToSSL();
        }
#endif

        public void Unread(byte[] buffer, int offset, int count)
        {
            if (null == buffer)
                throw new ArgumentNullException("buffer");
            if (offset < 0 || offset >= buffer.Length)
                throw new ArgumentOutOfRangeException("offset");
            if (count < 1 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException("count");

            if (!CanRead)
                throw new InvalidOperationException("Only readable streams can be read");

            CheckDisposed();

            if (null == _unreadData)
                _unreadData = new MemoryStream();
            else
                AdjustBuffer();

            _unreadData.Write(buffer, offset, count);

            _unreadData.Position -= count;
        }

        void AdjustBuffer()
        {
            var length = _unreadData.Length;

            if (0 == length)
                return;

            var position = (int)_unreadData.Position;

            if (position >= length)
            {
                _unreadData.Position = 0;
                _unreadData.SetLength(0);

                return;
            }

            if (position <= _unreadData.Capacity / 2)
                return;

            var unreadData = _unreadData.GetBuffer();

            var newLength = (int)length - position;

            Array.Copy(unreadData, position, unreadData, 0, newLength);

            _unreadData.Position = 0;
            _unreadData.SetLength(newLength);
        }
    }
}
