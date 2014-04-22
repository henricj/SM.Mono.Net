//
// System.Net.WebConnection
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
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

#if SECURITY_DEP

#if MONOTOUCH
using Mono.Security.Protocol.Tls;
#else
extern alias MonoSecurity;
using MonoSecurity::Mono.Security.Protocol.Tls;
#endif

#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using SM.Mono.Net.Sockets;
using SM.Mono.Utility;

namespace SM.Mono.Net
{
    enum ReadState
    {
        None,
        Status,
        Headers,
        Content,
        Aborted
    }

    class WebConnection
    {
        readonly ServicePoint _sPoint;
        StreamSocketStream _nstream;
        StreamSocket _socket;
        readonly object _socketLock = new object();
        readonly IWebConnectionState _state;
        WebExceptionStatus _status;
        bool _keepAlive;
        byte[] _buffer;
        readonly EventHandler _abortHandler;
        AbortHelper abortHelper;
        internal WebConnectionData Data;
        bool _chunkedRead;
        ChunkStream _chunkStream;
        readonly Queue<HttpWebRequest> _queue;
        bool _reused;
        int _position;
        HttpWebRequest _priorityRequest;
        NetworkCredential _ntlmCredentials;
        bool _ntlmAuthenticated;
        bool _unsafeSharing;

        enum NtlmAuthState
        {
            None,
            Challenge,
            Response
        }

        NtlmAuthState _connectNtlmAuthState;
        HttpWebRequest _connectRequest;

        bool _ssl;
        bool _certsAvailable;
        Exception _connectException;
        static readonly object ClassLock = new object();
        static Type _sslStream;
#if !MONOTOUCH
        static PropertyInfo _piClient;
        static PropertyInfo _piServer;
        static PropertyInfo _piTrustFailure;
#endif

#if MONOTOUCH
        [System.Runtime.InteropServices.DllImport ("__Internal")]
        static extern void monotouch_start_wwan (string uri);
#endif

        internal ChunkStream ChunkStream
        {
            get { return _chunkStream; }
        }

        public WebConnection(IWebConnectionState wcs, ServicePoint sPoint)
        {
            _state = wcs;
            _sPoint = sPoint;
            _buffer = new byte[4096];
            Data = new WebConnectionData();
            _queue = wcs.Group.Queue;
            abortHelper = new AbortHelper
                          {
                              Connection = this
                          };
            _abortHandler = abortHelper.Abort;
        }

        class AbortHelper
        {
            public WebConnection Connection;

            public void Abort(object sender, EventArgs args)
            {
                var other = ((HttpWebRequest)sender).WebConnection;
                if (other == null)
                    other = Connection;
                other.Abort(sender, args);
            }
        }

        bool CanReuse()
        {
            // The real condition is !(socket.Poll (0, SelectMode.SelectRead) || socket.Available != 0)
            // but if there's data pending to read (!) we won't reuse the socket.
            //return (socket.Poll (0, SelectMode.SelectRead) == false);
            return false;
        }

        async Task ConnectAsync(HttpWebRequest request)
        {
            var isLocked = false;
            try
            {
                Monitor.TryEnter(_socketLock, ref isLocked);

                if (_socket != null && _status == WebExceptionStatus.Success)
                {
                    // Take the chunked stream to the expected state (State.None)
                    if (CanReuse() && await CompleteChunkedReadAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        _reused = true;
                        return;
                    }
                }

                _reused = false;
                if (_socket != null)
                {
                    _socket.Dispose();
                    _socket = null;
                }

                _chunkStream = null;
            }
            finally
            {
                if (isLocked)
                    Monitor.Exit(_socketLock);
            }

            var hostEntry = await _sPoint.GetHostEntryAsync().ConfigureAwait(false);

            if (hostEntry == null)
            {
#if MONOTOUCH
                    monotouch_start_wwan (sPoint.Address.ToString ());
                    hostEntry = sPoint.HostEntry;
                    if (hostEntry == null) {
#endif
                _status = _sPoint.UsesProxy ? WebExceptionStatus.ProxyNameResolutionFailure :
                    WebExceptionStatus.NameResolutionFailure;
                return;
#if MONOTOUCH
                    }
#endif
            }

            //WebConnectionData data = Data;
            foreach (var address in hostEntry.AddressList)
            {
                lock (_socketLock)
                {
                    if (null != _socket)
                    {
                        _socket.Dispose();
                        _socket = null;
                    }

                    try
                    {
                        _socket = new StreamSocket();
                    }
                    catch (Exception se)
                    {
                        // The Socket ctor can throw if we run out of FD's
                        if (!request.Aborted)
                            _status = WebExceptionStatus.ConnectFailure;
                        _connectException = se;
                        return;
                    }

                    _socket.Control.NoDelay = !_sPoint.UseNagleAlgorithm;

                    try
                    {
                        _sPoint.KeepAliveSetup(_socket);
                    }
                    catch
                    {
                        // Ignore. Not supported in all platforms.
                    }
                }

                var remote = new IPEndPoint(address, _sPoint.Address.Port);

                IPEndPoint local;
                if (!_sPoint.GetLocalEndPointFromDelegate(remote, out local))
                {
                    StreamSocket s;
                    lock (_socketLock)
                    {
                        s = _socket;
                        _socket = null;
                        _status = WebExceptionStatus.ConnectFailure;
                    }

                    if (s != null)
                        s.Dispose();

                    return;
                }

                try
                {
                    if (request.Aborted)
                        return;

                    HostName localHostName = null;
                    var localServiceName = string.Empty;

                    if (null != local)
                    {
                        localHostName = new HostName(local.Address.ToString());

                        if (local.Port > 0)
                            localServiceName = local.Port.ToString(CultureInfo.InvariantCulture);
                    }

                    var remoteHostName = new HostName(remote.Address.ToString());
                    var remoteServiceName = remote.Port.ToString(CultureInfo.InvariantCulture);

                    var endpointPair = new EndpointPair(localHostName, localServiceName, remoteHostName, remoteServiceName);

                    await _socket.ConnectAsync(endpointPair).AsTask().ConfigureAwait(false);

                    _status = WebExceptionStatus.Success;

                    break;
                }
                catch (ThreadAbortException)
                {
                    // program exiting...
                    StreamSocket s;
                    lock (_socketLock)
                    {
                        s = _socket;
                        _socket = null;
                    }

                    if (s != null)
                        s.Dispose();

                    return;
                }
                catch (ObjectDisposedException)
                {
                    // socket closed from another thread
                    return;
                }
                catch (Exception exc)
                {
                    StreamSocket s;
                    lock (_socketLock)
                    {
                        s = _socket;
                        _socket = null;

                        if (!request.Aborted)
                            _status = WebExceptionStatus.ConnectFailure;
                    }

                    if (s != null)
                        s.Dispose();

                    _connectException = exc;
                }
            }
        }

        static void EnsureSSLStreamAvailable()
        {
            lock (ClassLock)
            {
                if (_sslStream != null)
                    return;

#if NET_2_1 && SECURITY_DEP
                sslStream = typeof (HttpsClientStream);
#else
                // HttpsClientStream is an internal glue class in Mono.Security.dll
                _sslStream = Type.GetType("Mono.Security.Protocol.Tls.HttpsClientStream, " +
                                          "" /*Consts.AssemblyMono_Security*/, false);

                if (_sslStream == null)
                {
                    var msg = "Missing Mono.Security.dll assembly. " +
                              "Support for SSL/TLS is unavailable.";

                    throw new NotSupportedException(msg);
                }
#endif
#if !MONOTOUCH
                _piClient = _sslStream.GetProperty("SelectedClientCertificate");
                _piServer = _sslStream.GetProperty("ServerCertificate");
                _piTrustFailure = _sslStream.GetProperty("TrustFailure");
#endif
            }
        }

        async Task<bool> CreateTunnelAsync(HttpWebRequest request, Uri connectUri,
            StreamSocketStream stream /*, out byte[] buffer */)
        {
            bool haveAuth;

            var connectBytes = CreateConnectBytes(request, connectUri, out haveAuth);

            await stream.WriteAsync(connectBytes, 0, connectBytes.Length).ConfigureAwait(false);

            var readHeaders = await ReadHeadersAsync(stream).ConfigureAwait(false);

            var status = readHeaders.Item1;
            var result = readHeaders.Item2;

            if ((!haveAuth || _connectNtlmAuthState == NtlmAuthState.Challenge) &&
                result != null && status == 407)
            {
                // Needs proxy auth
                var connectionHeader = result["Connection"];
                if (_socket != null && !string.IsNullOrEmpty(connectionHeader) &&
                    connectionHeader.ToLower() == "close")
                {
                    // The server is requesting that this connection be closed
                    _socket.Dispose();
                    _socket = null;
                }

                Data.StatusCode = status;
                Data.Challenge = result.GetValues_internal("Proxy-Authenticate", false);
                return false;
            }

            if (status != 200)
            {
                var msg = String.Format("The remote server returned a {0} status code.", status);
                HandleError(WebExceptionStatus.SecureChannelFailure, null, msg);
                return false;
            }

            return result != null;
        }

        byte[] CreateConnectBytes(HttpWebRequest request, Uri connectUri, out bool haveAuth)
        {
            var sb = new StringBuilder();

            sb.Append("CONNECT ");
            sb.Append(request.Address.Host);
            sb.Append(':');
            sb.Append(request.Address.Port);
            sb.Append(" HTTP/");
            if (request.ServicePoint.ProtocolVersion == HttpVersion.Version11)
                sb.Append("1.1");
            else
                sb.Append("1.0");

            sb.Append("\r\nHost: ");
            sb.Append(request.Address.Authority);

            var ntlm = false;
            var challenge = Data.Challenge;
            Data.Challenge = null;

            var authHeader = request.Headers["Proxy-Authorization"];
            haveAuth = authHeader != null;

            if (haveAuth)
            {
                sb.Append("\r\nProxy-Authorization: ");
                sb.Append(authHeader);
                ntlm = authHeader.ToUpper().Contains("NTLM");
            }
            else if (challenge != null && Data.StatusCode == 407)
            {
                var creds = request.Proxy.Credentials;
                haveAuth = true;

                if (_connectRequest == null)
                {
                    // create a CONNECT request to use with Authenticate
                    _connectRequest = (HttpWebRequest)WebRequest.Create(
                        connectUri.Scheme + "://" + connectUri.Host + ":" + connectUri.Port.ToString(CultureInfo.InvariantCulture) + "/");
                    _connectRequest.Method = "CONNECT";
                    _connectRequest.Credentials = creds;
                }

                foreach (var c in challenge)
                {
                    var auth = AuthenticationManager.Authenticate(c, _connectRequest, creds);
                    if (auth == null)
                        continue;
                    ntlm = (auth.Module.AuthenticationType == "NTLM");
                    sb.Append("\r\nProxy-Authorization: ");
                    sb.Append(auth.Message);
                    break;
                }
            }

            if (ntlm)
            {
                sb.Append("\r\nProxy-Connection: keep-alive");
                _connectNtlmAuthState++;
            }

            sb.Append("\r\n\r\n");

            Data.StatusCode = 0;

            var connectBytes = Encoding.UTF8.GetBytes(sb.ToString());

            return connectBytes;
        }

        async Task<Tuple<int, WebHeaderCollection>> ReadHeadersAsync(StreamSocketStream stream)
        {
            var status = 200;

            //var ms = new MemoryStream();
            var gotStatus = false;
            using (var lineReader = new HttpLineReader(stream))
            {
                while (true)
                {
                    //var n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    //if (n == 0)
                    //{
                    //    HandleError(WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders");
                    //    return null;
                    //}

                    //ms.Write(buffer, 0, n);
                    //var start = 0;
                    var headers = new WebHeaderCollection();
                    for (; ; )
                    {
                        var str = await lineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);

                        if (str == null)
                        {
                            var contentLen = 0L;
                            try
                            {
                                if (!long.TryParse(headers["Content-Length"], out contentLen))
                                    contentLen = 0;
                            }
                            catch
                            {
                                contentLen = 0;
                            }

                            lineReader.SyncStream();

                            //if (false) //ms.Length - start - contentLen > 0)
                            //{
                            //    // we've read more data than the response header and contents,
                            //    // give back extra data to the caller
                            //    //retBuffer = new byte[ms.Length - start - contentLen];
                            //    //Buffer.BlockCopy(ms.GetBuffer(), (int) (start + contentLen), retBuffer, 0, retBuffer.Length);
                            //}
                            if (contentLen > 0)
                            {
                                // haven't read in some or all of the contents for the response, do so now
                                await FlushContentsAsync(stream, contentLen).ConfigureAwait(false);
                            }

                            return Tuple.Create(status, headers);
                        }

                        if (gotStatus)
                        {
                            headers.Add(str);
                            continue;
                        }

                        var spaceidx = str.IndexOf(' ');
                        if (spaceidx == -1)
                        {
                            HandleError(WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders2");
                            return null;
                        }

                        status = (int)UInt32.Parse(str.Substring(spaceidx + 1, 3));
                        gotStatus = true;
                    }
                }
            }
        }

        async Task FlushContentsAsync(Stream stream, long contentLength)
        {
            var contentBuffer = new byte[Math.Min(4096, contentLength)];

            while (contentLength > 0)
            {
                var bytesRead = await stream.ReadAsync(contentBuffer, 0, (int)Math.Min(contentBuffer.Length, contentLength)).ConfigureAwait(false);
                if (bytesRead > 0)
                    contentLength -= bytesRead;
                else
                    break;
            }
        }

        async Task<bool> CreateStreamAsync(HttpWebRequest request)
        {
            try
            {
                var serverStream = new StreamSocketStream(_socket, false);

                if (request.Address.Scheme == Uri.UriSchemeHttps)
                {
                    _ssl = true;
                    EnsureSSLStreamAvailable();
                    if (!_reused || _nstream == null || _nstream.GetType() != _sslStream)
                    {
                        //byte[] buffer = null;
                        if (_sPoint.UseConnect)
                        {
                            var ok = await CreateTunnelAsync(request, _sPoint.Address, serverStream /*, out buffer */).ConfigureAwait(false);
                            if (!ok)
                                return false;
                        }
#if SECURITY_DEP
#if MONOTOUCH
                        _nstream = new HttpsClientStream (serverStream, request.ClientCertificates, request, buffer);
#else
                        object[] args = new object [4] { serverStream,
                            request.ClientCertificates,
                            request, buffer};
                        _nstream = (Stream) Activator.CreateInstance (sslStream, args);
#endif
                        SslClientStream scs = (SslClientStream) nstream;
                        var helper = new ServicePointManager.ChainValidationHelper (request, request.Address.Host);
                        scs.ServerCertValidation2 += new CertificateValidationCallback2 (helper.ValidateChain);
#endif
                        _certsAvailable = false;
                    }
                    // we also need to set ServicePoint.Certificate 
                    // and ServicePoint.ClientCertificate but this can
                    // only be done later (after handshake - which is
                    // done only after a read operation).
                }
                else
                {
                    _ssl = false;
                    _nstream = serverStream;
                }
            }
            catch (Exception)
            {
                if (!request.Aborted)
                    _status = WebExceptionStatus.ConnectFailure;
                return false;
            }

            return true;
        }

        void HandleError(WebExceptionStatus st, Exception e, string where)
        {
            _status = st;
            lock (this)
            {
                if (st == WebExceptionStatus.RequestCanceled)
                    Data = new WebConnectionData();
            }

            if (e == null)
            {
                // At least we now where it comes from
                try
                {
#if TARGET_JVM
                    throw new Exception ();
#else
                    throw new Exception(new StackTrace().ToString());
#endif
                }
                catch (Exception e2)
                {
                    e = e2;
                }
            }

            HttpWebRequest req = null;
            if (Data != null && Data.Request != null)
                req = Data.Request;

            Close(true);
            if (req != null)
            {
                req.FinishedReading = true;
                req.SetResponseError(st, e, where);
            }
        }

        static async Task ReadDoneAsync(WebConnection cnc, int nread)
        {
            var data = cnc.Data;
            var ns = cnc._nstream;
            if (ns == null)
            {
                cnc.Close(true);
                return;
            }

            //var nread = -1;
            //try
            //{
            //    nread = ns.EndRead(result);
            //}
            //catch (ObjectDisposedException)
            //{
            //    return;
            //}
            //catch (Exception e)
            //{
            //    if (e.InnerException is ObjectDisposedException)
            //        return;

            //    cnc.HandleError(WebExceptionStatus.ReceiveFailure, e, "ReadDone1");
            //    return;
            //}

            //if (nread == 0)
            //{
            //    cnc.HandleError(WebExceptionStatus.ReceiveFailure, null, "ReadDone2");
            //    return;
            //}

            //if (nread < 0)
            //{
            //    cnc.HandleError(WebExceptionStatus.ServerProtocolViolation, null, "ReadDone3");
            //    return;
            //}

            var pos = -1;
            nread += cnc._position;
            if (data.ReadState == ReadState.None)
            {
                Exception exc = null;
                try
                {
                    pos = await GetResponseAsync(cnc._nstream, data, cnc._sPoint).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    exc = e;
                }

                if (exc != null || pos == -1)
                {
                    cnc.HandleError(WebExceptionStatus.ServerProtocolViolation, exc, "ReadDone4");
                    return;
                }
            }

            if (data.ReadState == ReadState.Aborted)
            {
                cnc.HandleError(WebExceptionStatus.RequestCanceled, null, "ReadDone");
                return;
            }

            if (data.ReadState != ReadState.Content)
            {
                var est = nread * 2;
                var max = (est < cnc._buffer.Length) ? cnc._buffer.Length : est;
                var newBuffer = new byte[max];
                Buffer.BlockCopy(cnc._buffer, 0, newBuffer, 0, nread);
                cnc._buffer = newBuffer;
                cnc._position = nread;
                data.ReadState = ReadState.None;

                InitRead(cnc);

                return;
            }

            cnc._position = 0;

            var stream = new WebConnectionStream(cnc, data);
            var expectContent = ExpectContent(data.StatusCode, data.Request.Method);
            string tencoding = null;
            if (expectContent)
                tencoding = data.Headers["Transfer-Encoding"];

            cnc._chunkedRead = (tencoding != null && tencoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) != -1);
            if (!cnc._chunkedRead)
            {
                stream.ReadBuffer = cnc._buffer;
                stream.ReadBufferOffset = pos;
                stream.ReadBufferSize = nread;
                try
                {
                    await stream.CheckResponseInBufferAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    cnc.HandleError(WebExceptionStatus.ReceiveFailure, e, "ReadDone7");
                }
            }
            else if (cnc._chunkStream == null)
            {
                try
                {
                    cnc._chunkStream = new ChunkStream(cnc._buffer, pos, nread, data.Headers);
                }
                catch (Exception e)
                {
                    cnc.HandleError(WebExceptionStatus.ServerProtocolViolation, e, "ReadDone5");
                    return;
                }
            }
            else
            {
                cnc._chunkStream.ResetBuffer();
                try
                {
                    cnc._chunkStream.Write(cnc._buffer, pos, nread);
                }
                catch (Exception e)
                {
                    cnc.HandleError(WebExceptionStatus.ServerProtocolViolation, e, "ReadDone6");
                    return;
                }
            }

            data.Stream = stream;

            if (!expectContent)
                stream.ForceCompletion();

            await data.Request.SetResponseDataAsync(data).ConfigureAwait(false);
        }

        static bool ExpectContent(int statusCode, string method)
        {
            if (method == "HEAD")
                return false;
            return (statusCode >= 200 && statusCode != 204 && statusCode != 304);
        }

        internal void GetCertificates(Stream stream)
        {
            // here the SSL negotiation have been done
#if SECURITY_DEP && MONOTOUCH
            HttpsClientStream s = (stream as HttpsClientStream);
            X509Certificate client = s.SelectedClientCertificate;
            X509Certificate server = s.ServerCertificate;
#else
            var client = (X509Certificate)_piClient.GetValue(stream, null);
            var server = (X509Certificate)_piServer.GetValue(stream, null);
#endif
            _sPoint.SetCertificates(client, server);
            _certsAvailable = (server != null);
        }

        internal static void InitRead(WebConnection cnc)
        {
            StartRead(cnc)
                .ContinueWith(t =>
                              {
                                  if (t.IsFaulted)
                                      cnc.HandleError(WebExceptionStatus.UnknownError, t.Exception, "InitRead");
                              });
        }

        internal static async Task StartRead(WebConnection cnc)
        {
            var ns = cnc._nstream;

            try
            {
                //var size = cnc._buffer.Length - cnc._position;
                ////ns.BeginRead(cnc._buffer, cnc.position, size, readDoneDelegate, cnc);
                //var read = await ns.ReadAsync(cnc._buffer, cnc._position, size).ConfigureAwait(false);

                await ReadDoneAsync(cnc, 0);
            }
            catch (Exception e)
            {
                cnc.HandleError(WebExceptionStatus.ReceiveFailure, e, "StartRead");
            }
        }

        static async Task<int> GetResponseAsync(StreamSocketStream nstream, WebConnectionData data, ServicePoint sPoint)
        {
            var isContinue = false;
            var emptyFirstLine = false;

            using (var lineReader = new HttpLineReader(nstream))
            {
                do
                {
                    if (data.ReadState == ReadState.Aborted)
                        return -1;

                    if (data.ReadState == ReadState.None)
                    {
                        var line = await lineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);

                        if (null == line)
                            return 0;

                        if (string.IsNullOrEmpty(line))
                        {
                            emptyFirstLine = true;
                            continue;
                        }

                        emptyFirstLine = false;
                        data.ReadState = ReadState.Status;

                        var parts = line.Split(' ');
                        if (parts.Length < 2)
                            return -1;

                        if (string.Compare(parts[0], "HTTP/1.1", StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            data.Version = HttpVersion.Version11;
                            sPoint.SetVersion(HttpVersion.Version11);
                        }
                        else
                        {
                            data.Version = HttpVersion.Version10;
                            sPoint.SetVersion(HttpVersion.Version10);
                        }

                        data.StatusCode = (int)UInt32.Parse(parts[1]);
                        if (parts.Length >= 3)
                            data.StatusDescription = string.Join(" ", parts, 2, parts.Length - 2);
                        else
                            data.StatusDescription = "";
                    }

                    emptyFirstLine = false;
                    if (data.ReadState == ReadState.Status)
                    {
                        data.ReadState = ReadState.Headers;
                        data.Headers = new WebHeaderCollection();
                        var headers = new List<string>();

                        var finished = false;

                        while (!finished)
                        {
                            var line = await lineReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);

                            if (null == line)
                                break;

                            if (string.IsNullOrEmpty(line))
                            {
                                // Empty line: end of headers
                                finished = true;
                                continue;
                            }

                            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                            {
                                var count = headers.Count - 1;
                                if (count < 0)
                                    break;

                                var prev = headers[count] + line;
                                headers[count] = prev;
                            }
                            else
                                headers.Add(line);
                        }

                        if (!finished)
                            return 0;

                        lineReader.SyncStream();

                        foreach (var s in headers)
                            data.Headers.SetInternal(s);

                        if (data.StatusCode == (int)HttpStatusCode.Continue)
                        {
                            sPoint.SendContinue = true;

                            if (data.Request.ExpectContinue)
                            {
                                data.Request.DoContinueDelegate(data.StatusCode, data.Headers);
                                // Prevent double calls when getting the
                                // headers in several packets.
                                data.Request.ExpectContinue = false;
                            }

                            data.ReadState = ReadState.None;
                            isContinue = true;
                        }
                        else
                        {
                            data.ReadState = ReadState.Content;
                            return 1;
                        }
                    }
                } while (emptyFirstLine || isContinue);
            }

            return -1;
        }

        async Task InitConnectionAsync(HttpWebRequest request)
        {
            request.WebConnection = this;
            if (request.ReuseConnection)
                request.StoredConnection = this;

            if (request.Aborted)
                return;

            _keepAlive = request.KeepAlive;
            Data = new WebConnectionData(request);
        retry:
            await ConnectAsync(request).ConfigureAwait(false);

            if (request.Aborted)
                return;

            if (_status != WebExceptionStatus.Success)
            {
                if (!request.Aborted)
                {
                    request.SetWriteStreamError(_status, _connectException);
                    Close(true);
                }
                return;
            }

            if (!await CreateStreamAsync(request).ConfigureAwait(false))
            {
                if (request.Aborted)
                    return;

                var st = _status;
                if (Data.Challenge != null)
                    goto retry;

                var cncExc = _connectException;
                _connectException = null;
                request.SetWriteStreamError(st, cncExc);
                Close(true);
                return;
            }

            await request.SetWriteStreamAsync(new WebConnectionStream(this, request)).ConfigureAwait(false);
        }

#if MONOTOUCH
        static bool warned_about_queue = false;
#endif

        internal EventHandler SendRequest(HttpWebRequest request)
        {
            if (request.Aborted)
                return null;

            lock (this)
            {
                if (_state.TrySetBusy())
                {
                    _status = WebExceptionStatus.Success;
                    var task = InitConnectionAsync(request);

                    task.ContinueWith(t =>
                                      {
                                          var ex = t.Exception;
                                          if (null != ex)
                                              Debug.WriteLine("InitConnectionAsync failed: " + ex.Message);
                                      });
                }
                else
                {
                    lock (_queue)
                    {
#if MONOTOUCH
                        if (!warned_about_queue) {
                            warned_about_queue = true;
                            Console.WriteLine ("WARNING: An HttpWebRequest was added to the ConnectionGroup queue because the connection limit was reached.");
                        }
#endif
                        _queue.Enqueue(request);
                    }
                }
            }

            return _abortHandler;
        }

        void SendNext()
        {
            lock (_queue)
            {
                if (_queue.Count > 0)
                    SendRequest(_queue.Dequeue());
            }
        }

        internal void NextRead()
        {
            lock (this)
            {
                if (Data.Request != null)
                    Data.Request.FinishedReading = true;

                var header = _sPoint.UsesProxy ? "Proxy-Connection" : "Connection";
                var cncHeader = null != Data.Headers ? Data.Headers[header] : null;
                var keepAlive = Data.Version == HttpVersion.Version11 && _keepAlive;

                if (cncHeader != null)
                {
                    cncHeader = cncHeader.ToLower();
                    keepAlive = (_keepAlive && cncHeader.IndexOf("keep-alive", StringComparison.Ordinal) != -1);
                }

                if ((!keepAlive || (cncHeader != null && cncHeader.IndexOf("close", StringComparison.Ordinal) != -1)))
                    Close(false);

                _state.SetIdle();
                if (_priorityRequest != null)
                {
                    SendRequest(_priorityRequest);
                    _priorityRequest = null;
                }
                else
                    SendNext();
            }
        }

        //static async Task<string> ReadLineAsync(StreamSocketStream stream)
        //{
        //    var foundCR = false;
        //    var text = new StringBuilder();

        //    var c = (char) 0;
        //    while (start < max)
        //    {
        //        c = (char) buffer[start++];

        //        if (c == '\n')
        //        {
        //            // newline
        //            if ((text.Length > 0) && (text[text.Length - 1] == '\r'))
        //                text.Length--;

        //            foundCR = false;
        //            break;
        //        }
        //        if (foundCR)
        //        {
        //            text.Length--;
        //            break;
        //        }

        //        if (c == '\r')
        //            foundCR = true;

        //        text.Append(c);
        //    }

        //    if (c != '\n' && c != '\r')
        //        return false;

        //    if (text.Length == 0)
        //    {
        //        output = null;
        //        return (c == '\n' || c == '\r');
        //    }

        //    if (foundCR)
        //        text.Length--;

        //    output = text.ToString();

        //    return true;
        //}

        internal async Task<int> ReadAsync(HttpWebRequest request, byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            StreamSocketStream s = null;
            lock (this)
            {
                if (Data.Request != request)
                    throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
                if (_nstream == null)
                    throw new InvalidOperationException("No stream available");
                s = _nstream;
            }

            //IAsyncResult result = null;
            var nbytes = 0;
            var done = false;
            if (!_chunkedRead || (!_chunkStream.DataAvailable && _chunkStream.WantMore))
            {
                try
                {
                    nbytes = await s.ReadAsync(buffer, offset, size, cancellationToken).ConfigureAwait(false);
                    done = nbytes == 0;

                    //result = s.BeginRead(buffer, offset, size, cb, state);
                    //cb = null;
                }
                catch (Exception)
                {
                    HandleError(WebExceptionStatus.ReceiveFailure, null, "chunked ReadAsync");
                    throw;
                }
            }

            if (_chunkedRead)
                return -1; // TODO: What should this return?
            //{
            //    var wr = new WebAsyncResult(cb, state, buffer, offset, size);
            //    wr.InnerAsyncResult = result;
            //    if (result == null)
            //    {
            //        // Will be completed from the data in ChunkStream
            //        wr.SetCompleted(true, (Exception) null);
            //        wr.DoCallback();
            //    }
            //    return wr;
            //}

            lock (this)
            {
                if (Data.Request != request)
                    throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
                if (_nstream == null)
                    throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
                s = _nstream;
            }

            //WebAsyncResult wr = null;
            //var nsAsync = ((WebAsyncResult) result).InnerAsyncResult;
            //if (_chunkedRead && (nsAsync is WebAsyncResult))
            //{
            //    wr = (WebAsyncResult) nsAsync;
            //    var inner = wr.InnerAsyncResult;
            //    if (inner != null && !(inner is WebAsyncResult))
            //    {
            //        nbytes = s.EndRead(inner);
            //        done = nbytes == 0;
            //    }
            //}
            //else if (!(nsAsync is WebAsyncResult))
            //{
            //    nbytes = s.EndRead(nsAsync);
            //    wr = (WebAsyncResult) result;
            //    done = nbytes == 0;
            //}

            if (_chunkedRead)
            {
                try
                {
                    _chunkStream.WriteAndReadBack(buffer, offset, size, ref nbytes);
                    if (!done && nbytes == 0 && _chunkStream.WantMore)
                        nbytes = await EnsureReadAsync(buffer, offset, size).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (e is WebException)
                        throw;

                    throw new WebException("Invalid chunked data.", e,
                        WebExceptionStatus.ServerProtocolViolation, null);
                }

                if ((done || nbytes == 0) && _chunkStream.ChunkLeft != 0)
                {
                    HandleError(WebExceptionStatus.ReceiveFailure, null, "chunked EndRead");
                    throw new WebException("Read error", null, WebExceptionStatus.ReceiveFailure, null);
                }
            }

            return (nbytes != 0) ? nbytes : -1;
        }

        // To be called on chunkedRead when we can read no data from the ChunkStream yet
        async Task<int> EnsureReadAsync(byte[] buffer, int offset, int size)
        {
            byte[] morebytes = null;
            var nbytes = 0;
            while (nbytes == 0 && _chunkStream.WantMore)
            {
                var localsize = _chunkStream.ChunkLeft;
                if (localsize <= 0) // not read chunk size yet
                    localsize = 1024;
                else if (localsize > 16384)
                    localsize = 16384;

                if (morebytes == null || morebytes.Length < localsize)
                    morebytes = new byte[localsize];

                var nread = await _nstream.ReadAsync(morebytes, 0, localsize, CancellationToken.None).ConfigureAwait(false);

                if (nread <= 0)
                    return 0; // Error

                _chunkStream.Write(morebytes, 0, nread);
                nbytes += _chunkStream.Read(buffer, offset + nbytes, size - nbytes);
            }

            return nbytes;
        }

        async Task<bool> CompleteChunkedReadAsync(CancellationToken cancellationToken)
        {
            if (!_chunkedRead || _chunkStream == null)
                return true;

            while (_chunkStream.WantMore)
            {
                var nbytes = await _nstream.ReadAsync(_buffer, 0, _buffer.Length, cancellationToken).ConfigureAwait(false);
                if (nbytes <= 0)
                    return false; // Socket was disconnected

                _chunkStream.Write(_buffer, 0, nbytes);
            }

            return true;
        }

        internal async Task FlushAsync(HttpWebRequest request, CancellationToken cancellationToken)
        {
            Stream s;

            lock (this)
            {
                if (Data.Request != request)
                    throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
                if (_nstream == null)
                    throw new InvalidOperationException("No stream available");
                s = _nstream;
            }

            try
            {
                await s.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                _status = WebExceptionStatus.SendFailure;

                if (exc.InnerException != null)
                    throw exc.InnerException;
            }
        }

        internal async Task<bool> WriteAsync(HttpWebRequest request, byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            Stream s;

            lock (this)
            {
                if (Data.Request != request)
                    throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
                if (_nstream == null)
                    throw new InvalidOperationException("No stream available");
                s = _nstream;
            }

            try
            {
                await s.WriteAsync(buffer, offset, size, cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (Exception exc)
            {
                _status = WebExceptionStatus.SendFailure;

                if (exc.InnerException != null)
                    throw exc.InnerException;
            }

            return request.FinishedReading;
        }

        //internal IAsyncResult BeginWrite(HttpWebRequest request, byte[] buffer, int offset, int size, AsyncCallback cb, object state)
        //{
        //    Stream s = null;
        //    lock (this)
        //    {
        //        if (Data.Request != request)
        //            throw new ObjectDisposedException(typeof (StreamSocketStream).FullName);
        //        if (_nstream == null)
        //            return null;
        //        s = _nstream;
        //    }

        //    IAsyncResult result = null;
        //    try
        //    {
        //        result = s.BeginWrite(buffer, offset, size, cb, state);
        //        var x = s.WriteAsync(buffer, offset, size).ConfigureAwait(false)
        //    }
        //    catch (Exception)
        //    {
        //        _status = WebExceptionStatus.SendFailure;
        //        throw;
        //    }

        //    return result;
        //}

        //internal bool EndWrite(HttpWebRequest request, bool throwOnError, IAsyncResult result)
        //{
        //    if (request.FinishedReading)
        //        return true;

        //    Stream s = null;
        //    lock (this)
        //    {
        //        if (Data.Request != request)
        //            throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
        //        if (_nstream == null)
        //            throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
        //        s = _nstream;
        //    }

        //    try
        //    {
        //        s.EndWrite(result);
        //        return true;
        //    }
        //    catch (Exception exc)
        //    {
        //        _status = WebExceptionStatus.SendFailure;
        //        if (throwOnError && exc.InnerException != null)
        //            throw exc.InnerException;
        //        return false;
        //    }
        //}

        //internal int Read(HttpWebRequest request, byte[] buffer, int offset, int size)
        //{
        //    Stream s = null;
        //    lock (this)
        //    {
        //        if (Data.Request != request)
        //            throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
        //        if (_nstream == null)
        //            return 0;
        //        s = _nstream;
        //    }

        //    var result = 0;
        //    try
        //    {
        //        var done = false;
        //        if (!_chunkedRead)
        //        {
        //            result = s.Read(buffer, offset, size);
        //            done = (result == 0);
        //        }

        //        if (_chunkedRead)
        //        {
        //            try
        //            {
        //                _chunkStream.WriteAndReadBack(buffer, offset, size, ref result);
        //                if (!done && result == 0 && _chunkStream.WantMore)
        //                    result = EnsureReadAsync(buffer, offset, size);
        //            }
        //            catch (Exception e)
        //            {
        //                HandleError(WebExceptionStatus.ReceiveFailure, e, "chunked Read1");
        //                throw;
        //            }

        //            if ((done || result == 0) && _chunkStream.WantMore)
        //            {
        //                HandleError(WebExceptionStatus.ReceiveFailure, null, "chunked Read2");
        //                throw new WebException("Read error", null, WebExceptionStatus.ReceiveFailure, null);
        //            }
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        HandleError(WebExceptionStatus.ReceiveFailure, e, "Read");
        //    }

        //    return result;
        //}

        internal bool Write(HttpWebRequest request, byte[] buffer, int offset, int size, ref string err_msg)
        {
            err_msg = null;
            Stream s = null;
            lock (this)
            {
                if (Data.Request != request)
                    throw new ObjectDisposedException(typeof(StreamSocketStream).FullName);
                s = _nstream;
                if (s == null)
                    return false;
            }

            try
            {
                s.Write(buffer, offset, size);
                // here SSL handshake should have been done
                if (_ssl && !_certsAvailable)
                    GetCertificates(s);
            }
            catch (Exception e)
            {
                err_msg = e.Message;
                var wes = WebExceptionStatus.SendFailure;
                var msg = "Write: " + err_msg;
                if (e is WebException)
                {
                    HandleError(wes, e, msg);
                    return false;
                }

                // if SSL is in use then check for TrustFailure
                if (_ssl)
                {
#if SECURITY_DEP && MONOTOUCH
                    HttpsClientStream https = (s as HttpsClientStream);
                    if (https.TrustFailure) {
#else
                    if ((bool)_piTrustFailure.GetValue(s, null))
                    {
#endif
                        wes = WebExceptionStatus.TrustFailure;
                        msg = "Trust failure";
                    }
                }

                HandleError(wes, e, msg);
                return false;
            }
            return true;
        }

        internal void Close(bool sendNext)
        {
            lock (this)
            {
                if (Data != null && Data.Request != null && Data.Request.ReuseConnection)
                {
                    Data.Request.ReuseConnection = false;
                    return;
                }

                if (_nstream != null)
                {
                    try
                    {
                        _nstream.Close();
                    }
                    catch
                    { }
                    _nstream = null;
                }

                if (_socket != null)
                {
                    try
                    {
                        _socket.Dispose();
                    }
                    catch
                    { }
                    _socket = null;
                }

                if (_ntlmAuthenticated)
                    ResetNtlm();
                if (Data != null)
                {
                    lock (Data)
                    {
                        Data.ReadState = ReadState.Aborted;
                    }
                }
                _state.SetIdle();
                Data = new WebConnectionData();
                if (sendNext)
                    SendNext();

                _connectRequest = null;
                _connectNtlmAuthState = NtlmAuthState.None;
            }
        }

        void Abort(object sender, EventArgs args)
        {
            lock (this)
            {
                lock (_queue)
                {
                    var req = (HttpWebRequest)sender;
                    if (Data.Request == req || Data.Request == null)
                    {
                        if (!req.FinishedReading)
                        {
                            _status = WebExceptionStatus.RequestCanceled;
                            Close(false);
                            if (_queue.Count > 0)
                            {
                                Data.Request = _queue.Dequeue();
                                SendRequest(Data.Request);
                            }
                        }
                        return;
                    }

                    req.FinishedReading = true;
                    req.SetResponseError(WebExceptionStatus.RequestCanceled, null, "User aborted");
                    if (_queue.Count > 0 && _queue.Peek() == sender)
                        _queue.Dequeue();
                    else if (_queue.Count > 0)
                    {
                        var old = _queue.ToArray();
                        _queue.Clear();
                        for (var i = old.Length - 1; i >= 0; i--)
                        {
                            if (old[i] != sender)
                                _queue.Enqueue(old[i]);
                        }
                    }
                }
            }
        }

        internal void ResetNtlm()
        {
            _ntlmAuthenticated = false;
            _ntlmCredentials = null;
            _unsafeSharing = false;
        }

        internal bool Connected
        {
            get
            {
                lock (this)
                {
                    return _socket != null;
                }
            }
        }

        // -Used for NTLM authentication
        internal HttpWebRequest PriorityRequest
        {
            set { _priorityRequest = value; }
        }

        internal bool NtlmAuthenticated
        {
            get { return _ntlmAuthenticated; }
            set { _ntlmAuthenticated = value; }
        }

        internal NetworkCredential NtlmCredential
        {
            get { return _ntlmCredentials; }
            set { _ntlmCredentials = value; }
        }

        internal bool UnsafeAuthenticatedConnectionSharing
        {
            get { return _unsafeSharing; }
            set { _unsafeSharing = value; }
        }

        // -
    }
}
