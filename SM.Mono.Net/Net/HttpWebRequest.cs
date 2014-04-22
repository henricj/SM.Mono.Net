//
// System.Net.HttpWebRequest
//
// Authors:
// 	Lawrence Pit (loz@cable.a2000.nl)
// 	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (c) 2002 Lawrence Pit
// (c) 2003 Ximian, Inc. (http://www.ximian.com)
// (c) 2004 Novell, Inc. (http://www.novell.com)
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

//using System.Configuration;
//using System.Net.Cache;
//using System.Runtime.Remoting.Messaging;

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SM.Mono.Net.Cache;
using SM.Mono.Security.Cryptography.X509Certificates;

namespace SM.Mono.Net
{
    public class HttpWebRequest : WebRequest
    {
        readonly Uri _requestUri;
        bool _hostChanged;
        X509CertificateCollection _certificates;
        string _connectionGroup;
        long _contentLength = -1;
        ICredentials _credentials;
        bool _haveResponse;
        bool _haveRequest;
        bool _requestSent;
        WebHeaderCollection _webHeaders;
        int _maxAutoRedirect = 50;
        string _method = "GET";
        string _initialMethod = "GET";
        bool _preAuthenticate;
        bool _usedPreAuth;
        Version _version = HttpVersion.Version11;
        bool _forceVersion;
        Version _actualVersion;
        IWebProxy _proxy;
        bool _sendChunked;
        ServicePoint _servicePoint;
        int _timeout = 100000;
        internal bool ReuseConnection { get; set; }
        internal WebConnection StoredConnection;
        HttpWebResponse _previousWebResponse;
        WebConnectionStream _writeStream;
        HttpWebResponse _webResponse;
        TaskCompletionSource<Stream> _asyncWrite;
        TaskCompletionSource<HttpWebResponse> _asyncRead;
        EventHandler _abortHandler;
        int _aborted;
        bool _gotRequestStream;
        int _redirects;
        byte[] _bodyBuffer;
        int _bodyBufferLength;
        bool _getResponseCalled;
        Exception _savedExc;
        readonly object _locker = new object();
        bool isBusy;
        internal WebConnection WebConnection;
        DecompressionMethods _autoDecomp;
        int _maxResponseHeadersLength;
        static int _defaultMaxResponseHeadersLength;
        int _readWriteTimeout = 300000; // ms

        enum NtlmAuthState
        {
            None,
            Challenge,
            Response
        }

        AuthorizationState _authState, _proxyAuthState;
        string _host;

        // Constructors
        static HttpWebRequest()
        {
            _defaultMaxResponseHeadersLength = 64 * 1024;
#if !NET_2_1
            //NetConfig config = ConfigurationSettings.GetConfig ("system.net/settings") as NetConfig;
            //if (config != null) {
            //    int x = config.MaxResponseHeadersLength;
            //    if (x != -1)
            //        x *= 64;

            //    defaultMaxResponseHeadersLength = x;
            //}
#endif
        }

#if NET_2_1
        public
#else
        internal
#endif
 HttpWebRequest(Uri uri)
        {
            AllowAutoRedirect = true;
            AllowWriteStreamBuffering = true;
            KeepAlive = true;
            MediaType = String.Empty;
            Pipelined = true;
            _requestUri = uri;
            Address = uri;
            _proxy = GlobalProxySelection.Select;
            _webHeaders = new WebHeaderCollection(WebHeaderCollection.HeaderInfo.Request);
            ThrowOnError = true;
            ResetAuthorization();
        }

        void ResetAuthorization()
        {
            _authState = new AuthorizationState(this, false);
            _proxyAuthState = new AuthorizationState(this, true);
        }

        // Properties

        public string Accept
        {
            get { return _webHeaders["Accept"]; }
            set
            {
                CheckRequestStarted();
                _webHeaders.RemoveAndAdd("Accept", value);
            }
        }

        public Uri Address { get; internal set; }

        public bool AllowAutoRedirect { get; set; }

        public bool AllowWriteStreamBuffering { get; set; }

#if NET_4_5
        public virtual bool AllowReadStreamBuffering {
            get { return allowBuffering; }
            set { allowBuffering = value; }
        }
#endif

        static Exception GetMustImplement()
        {
            return new NotImplementedException();
        }

        public DecompressionMethods AutomaticDecompression
        {
            get { return _autoDecomp; }
            set
            {
                CheckRequestStarted();
                _autoDecomp = value;
            }
        }

        internal bool InternalAllowBuffering
        {
            get
            {
                return (AllowWriteStreamBuffering && (_method != "HEAD" && _method != "GET" &&
                                                      _method != "MKCOL" && _method != "CONNECT" &&
                                                      _method != "TRACE"));
            }
        }

        public X509CertificateCollection ClientCertificates
        {
            get
            {
                if (_certificates == null)
                    _certificates = new X509CertificateCollection();

                return _certificates;
            }
            [MonoTODO]
            set { throw GetMustImplement(); }
        }

        public string Connection
        {
            get { return _webHeaders["Connection"]; }
            set
            {
                CheckRequestStarted();

                if (string.IsNullOrEmpty(value))
                {
                    _webHeaders.RemoveInternal("Connection");
                    return;
                }

                var val = value.ToLowerInvariant();
                if (val.Contains("keep-alive") || val.Contains("close"))
                    throw new ArgumentException("Keep-Alive and Close may not be set with this property");

                if (KeepAlive)
                    value = value + ", Keep-Alive";

                _webHeaders.RemoveAndAdd("Connection", value);
            }
        }

        public override string ConnectionGroupName
        {
            get { return _connectionGroup; }
            set { _connectionGroup = value; }
        }

        public override long ContentLength
        {
            get { return _contentLength; }
            set
            {
                CheckRequestStarted();
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value", "Content-Length must be >= 0");

                _contentLength = value;
            }
        }

        internal long InternalContentLength
        {
            set { _contentLength = value; }
        }

        bool ThrowOnError { get; set; }

        public override string ContentType
        {
            get { return _webHeaders["Content-Type"]; }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _webHeaders.RemoveInternal("Content-Type");
                    return;
                }
                _webHeaders.RemoveAndAdd("Content-Type", value);
            }
        }

        public HttpContinueDelegate ContinueDelegate { get; set; }

#if NET_4_5
        virtual
#endif

        public CookieContainer CookieContainer { get; set; }

        public override ICredentials Credentials
        {
            get { return _credentials; }
            set { _credentials = value; }
        }

#if NET_4_0
        public DateTime Date {
            get {
                string date = webHeaders ["Date"];
                if (date == null)
                    return DateTime.MinValue;
                return DateTime.ParseExact (date, "r", CultureInfo.InvariantCulture).ToLocalTime ();
            }
            set {
                if (value.Equals (DateTime.MinValue))
                    webHeaders.RemoveInternal ("Date");
                else
                    webHeaders.RemoveAndAdd ("Date", value.ToUniversalTime ().ToString ("r", CultureInfo.InvariantCulture));
            }
        }
#endif

#if !NET_2_1
        [MonoTODO]
        public new static RequestCachePolicy DefaultCachePolicy
        {
            get { throw GetMustImplement(); }
            set { throw GetMustImplement(); }
        }
#endif

        [MonoTODO]
        public static int DefaultMaximumErrorResponseLength
        {
            get { throw GetMustImplement(); }
            set { throw GetMustImplement(); }
        }

        public string Expect
        {
            get { return _webHeaders["Expect"]; }
            set
            {
                CheckRequestStarted();
                var val = value;
                if (val != null)
                    val = val.Trim().ToLower();

                if (string.IsNullOrEmpty(val))
                {
                    _webHeaders.RemoveInternal("Expect");
                    return;
                }

                if (val == "100-continue")
                {
                    throw new ArgumentException("100-Continue cannot be set with this property.",
                        "value");
                }
                _webHeaders.RemoveAndAdd("Expect", value);
            }
        }

#if NET_4_5
        virtual
#endif

        public bool HaveResponse
        {
            get { return _haveResponse; }
        }

        public override WebHeaderCollection Headers
        {
            get { return _webHeaders; }
            set
            {
                CheckRequestStarted();
                var newHeaders = new WebHeaderCollection(WebHeaderCollection.HeaderInfo.Request);
                var count = value.Count;
                for (var i = 0; i < count; i++)
                    newHeaders.Add(value.GetKey(i), value.Get(i));

                _webHeaders = newHeaders;
            }
        }

#if NET_4_0
        public
#else
        internal
#endif
 string Host
        {
            get
            {
                if (_host == null)
                    return Address.Authority;
                return _host;
            }
            set
            {
                if (value == null)
                    throw new ArgumentNullException("value");

                if (!CheckValidHost(Address.Scheme, value))
                    throw new ArgumentException("Invalid host: " + value);

                _host = value;
            }
        }

        static bool CheckValidHost(string scheme, string val)
        {
            if (val.Length == 0)
                return false;

            if (val[0] == '.')
                return false;

            var idx = val.IndexOf('/');
            if (idx >= 0)
                return false;

            IPAddress ipaddr;
            if (IPAddress.TryParse(val, out ipaddr))
                return true;

            var u = scheme + "://" + val + "/";
            return Uri.IsWellFormedUriString(u, UriKind.Absolute);
        }

        public DateTime IfModifiedSince
        {
            get
            {
                var str = _webHeaders["If-Modified-Since"];
                if (str == null)
                    return DateTime.Now;
                try
                {
                    return MonoHttpDate.Parse(str);
                }
                catch (Exception)
                {
                    return DateTime.Now;
                }
            }
            set
            {
                CheckRequestStarted();
                // rfc-1123 pattern
                _webHeaders.SetInternal("If-Modified-Since",
                    value.ToUniversalTime().ToString("r", null));
                // TODO: check last param when using different locale
            }
        }

        public bool KeepAlive { get; set; }

        public int MaximumAutomaticRedirections
        {
            get { return _maxAutoRedirect; }
            set
            {
                if (value <= 0)
                    throw new ArgumentException("Must be > 0", "value");

                _maxAutoRedirect = value;
            }
        }

        [MonoTODO("Use this")]
        public int MaximumResponseHeadersLength
        {
            get { return _maxResponseHeadersLength; }
            set { _maxResponseHeadersLength = value; }
        }

        [MonoTODO("Use this")]
        public static int DefaultMaximumResponseHeadersLength
        {
            get { return _defaultMaxResponseHeadersLength; }
            set { _defaultMaxResponseHeadersLength = value; }
        }

        public int ReadWriteTimeout
        {
            get { return _readWriteTimeout; }
            set
            {
                if (_requestSent)
                    throw new InvalidOperationException("The request has already been sent.");

                if (value < -1)
                    throw new ArgumentOutOfRangeException("value", "Must be >= -1");

                _readWriteTimeout = value;
            }
        }

#if NET_4_5
        [MonoTODO]
        public int ContinueTimeout {
            get { throw new NotImplementedException (); }
            set { throw new NotImplementedException (); }
        }
#endif

        public string MediaType { get; set; }

        public override string Method
        {
            get { return _method; }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("not a valid method");

                _method = value.ToUpperInvariant();
                if (_method != "HEAD" && _method != "GET" && _method != "POST" && _method != "PUT" &&
                    _method != "DELETE" && _method != "CONNECT" && _method != "TRACE" &&
                    _method != "MKCOL")
                    _method = value;
            }
        }

        public bool Pipelined { get; set; }

        public override bool PreAuthenticate
        {
            get { return _preAuthenticate; }
            set { _preAuthenticate = value; }
        }

        public Version ProtocolVersion
        {
            get { return _version; }
            set
            {
                if (value != HttpVersion.Version10 && value != HttpVersion.Version11)
                    throw new ArgumentException("value");

                _forceVersion = true;
                _version = value;
            }
        }

        public override IWebProxy Proxy
        {
            get { return _proxy; }
            set
            {
                CheckRequestStarted();
                _proxy = value;
                _servicePoint = null; // we may need a new one
            }
        }

        public string Referer
        {
            get { return _webHeaders["Referer"]; }
            set
            {
                CheckRequestStarted();
                if (string.IsNullOrWhiteSpace(value))
                {
                    _webHeaders.RemoveInternal("Referer");
                    return;
                }
                _webHeaders.SetInternal("Referer", value);
            }
        }

        public override Uri RequestUri
        {
            get { return _requestUri; }
        }

        public bool SendChunked
        {
            get { return _sendChunked; }
            set
            {
                CheckRequestStarted();
                _sendChunked = value;
            }
        }

        public ServicePoint ServicePoint
        {
            get { return GetServicePoint(); }
        }

        internal ServicePoint ServicePointNoLock
        {
            get { return _servicePoint; }
        }

#if NET_4_0
        public virtual bool SupportsCookieContainer { 
            get {
                // The managed implementation supports the cookie container
                // it is only Silverlight that returns false here
                return true;
            }
        }
#endif

        public override int Timeout
        {
            get { return _timeout; }
            set
            {
                if (value < -1)
                    throw new ArgumentOutOfRangeException("value");

                _timeout = value;
            }
        }

        public string TransferEncoding
        {
            get { return _webHeaders["Transfer-Encoding"]; }
            set
            {
                CheckRequestStarted();
                var val = value;
                if (val != null)
                    val = val.Trim().ToLower();

                if (string.IsNullOrEmpty(value))
                {
                    _webHeaders.RemoveInternal("Transfer-Encoding");
                    return;
                }

                if (val == "chunked")
                    throw new ArgumentException("Chunked encoding must be set with the SendChunked property");

                if (!_sendChunked)
                    throw new ArgumentException("SendChunked must be True", "value");

                _webHeaders.RemoveAndAdd("Transfer-Encoding", value);
            }
        }

        public override bool UseDefaultCredentials
        {
            get { return CredentialCache.DefaultCredentials == Credentials; }
            set { Credentials = value ? CredentialCache.DefaultCredentials : null; }
        }

        public string UserAgent
        {
            get { return _webHeaders["User-Agent"]; }
            set { _webHeaders.SetInternal("User-Agent", value); }
        }

        public bool UnsafeAuthenticatedConnectionSharing { get; set; }

        internal bool GotRequestStream
        {
            get { return _gotRequestStream; }
        }

        internal bool ExpectContinue { get; set; }

        internal Uri AuthUri
        {
            get { return Address; }
        }

        internal bool ProxyQuery
        {
            get { return _servicePoint.UsesProxy && !_servicePoint.UseConnect; }
        }

        // Methods

        internal ServicePoint GetServicePoint()
        {
            lock (_locker)
            {
                if (_hostChanged || _servicePoint == null)
                {
                    _servicePoint = ServicePointManager.FindServicePoint(Address, _proxy);
                    _hostChanged = false;
                }
            }

            return _servicePoint;
        }

        public void AddRange(int range)
        {
            AddRange("bytes", (long)range);
        }

        public void AddRange(int from, int to)
        {
            AddRange("bytes", @from, (long)to);
        }

        public void AddRange(string rangeSpecifier, int range)
        {
            AddRange(rangeSpecifier, (long)range);
        }

        public void AddRange(string rangeSpecifier, int from, int to)
        {
            AddRange(rangeSpecifier, @from, (long)to);
        }

#if NET_4_0
        public
#else
        internal
#endif
 void AddRange(long range)
        {
            AddRange("bytes", range);
        }

#if NET_4_0
        public
#else
        internal
#endif
 void AddRange(long from, long to)
        {
            AddRange("bytes", from, to);
        }

#if NET_4_0
        public
#else
        internal
#endif
 void AddRange(string rangeSpecifier, long range)
        {
            if (rangeSpecifier == null)
                throw new ArgumentNullException("rangeSpecifier");
            if (!WebHeaderCollection.IsHeaderValue(rangeSpecifier))
                throw new ArgumentException("Invalid range specifier", "rangeSpecifier");

            var r = _webHeaders["Range"];
            if (r == null)
                r = rangeSpecifier + "=";
            else
            {
                var old_specifier = r.Substring(0, r.IndexOf('='));
                if (String.Compare(old_specifier, rangeSpecifier, StringComparison.OrdinalIgnoreCase) != 0)
                    throw new InvalidOperationException("A different range specifier is already in use");
                r += ",";
            }

            var n = range.ToString(CultureInfo.InvariantCulture);
            if (range < 0)
                r = r + "0" + n;
            else
                r = r + n + "-";
            _webHeaders.RemoveAndAdd("Range", r);
        }

#if NET_4_0
        public
#else
        internal
#endif
 void AddRange(string rangeSpecifier, long from, long to)
        {
            if (rangeSpecifier == null)
                throw new ArgumentNullException("rangeSpecifier");
            if (!WebHeaderCollection.IsHeaderValue(rangeSpecifier))
                throw new ArgumentException("Invalid range specifier", "rangeSpecifier");
            if (from > to || from < 0)
                throw new ArgumentOutOfRangeException("from");
            if (to < 0)
                throw new ArgumentOutOfRangeException("to");

            var r = _webHeaders["Range"];
            if (r == null)
                r = rangeSpecifier + "=";
            else
                r += ",";

            r = String.Format("{0}{1}-{2}", r, from, to);
            _webHeaders.RemoveAndAdd("Range", r);
        }

        public override Task<Stream> GetRequestStreamAsync(CancellationToken cancellationToken)
        {
            if (Aborted)
                throw new WebException("The request was canceled.", WebExceptionStatus.RequestCanceled);

            var send = !(_method == "GET" || _method == "CONNECT" || _method == "HEAD" ||
                         _method == "TRACE");
            if (_method == null || !send)
                throw new ProtocolViolationException("Cannot send data when method is: " + _method);

            if (_contentLength == -1 && !_sendChunked && !AllowWriteStreamBuffering && KeepAlive)
                throw new ProtocolViolationException("Content-Length not set");

            var transferEncoding = TransferEncoding;
            if (!_sendChunked && transferEncoding != null && transferEncoding.Trim() != "")
                throw new ProtocolViolationException("SendChunked should be true.");

            lock (_locker)
            {
                if (_getResponseCalled)
                    throw new InvalidOperationException("The operation cannot be performed once the request has been submitted.");

                if (isBusy)
                {
                    throw new InvalidOperationException("Cannot re-call start of asynchronous " +
                                                        "method while a previous call is still in progress.");
                }

                _initialMethod = _method;

                if (null != _asyncWrite)
                    return _asyncWrite.Task;

                //if (haveRequest)
                //{
                //    if (writeStream != null)
                //        return writeStream;
                //}

                _gotRequestStream = true;

                _asyncWrite = new TaskCompletionSource<Stream>();

                if (!_requestSent)
                {
                    _requestSent = true;
                    _redirects = 0;
                    _servicePoint = GetServicePoint();
                    _abortHandler = _servicePoint.SendRequest(this, _connectionGroup);
                }

                return _asyncWrite.Task;
            }
        }

        Task CheckIfForceWriteAsync()
        {
            if (_writeStream == null || _writeStream.RequestWritten || !InternalAllowBuffering)
                return null;
#if NET_4_0
            if (contentLength < 0 && writeStream.CanWrite == true && writeStream.WriteBufferLength < 0)
                return null;

            if (contentLength < 0 && writeStream.WriteBufferLength >= 0)
                InternalContentLength = writeStream.WriteBufferLength;
#else
            if (_contentLength < 0 && _writeStream.CanWrite)
                return null;
#endif

            // This will write the POST/PUT if the write stream already has the expected
            // amount of bytes in it (ContentLength) (bug #77753) or if the write stream
            // contains data and it has been closed already (xamarin bug #1512).

            if (_writeStream.WriteBufferLength == _contentLength || (_contentLength == -1 && _writeStream.CanWrite == false))
                return _writeStream.WriteRequestAsync();

            return null;
        }

        public override Task<HttpWebResponse> GetResponseAsync(CancellationToken cancellationToken)
        {
            if (Aborted)
                throw new WebException("The request was canceled.", WebExceptionStatus.RequestCanceled);

            if (_method == null)
                throw new ProtocolViolationException("Method is null.");

            var transferEncoding = TransferEncoding;
            if (!_sendChunked && !string.IsNullOrWhiteSpace(transferEncoding))
                throw new ProtocolViolationException("SendChunked should be true.");

            Monitor.Enter(_locker);
            _getResponseCalled = true;
            if (_asyncRead != null && !_haveResponse)
            {
                Monitor.Exit(_locker);
                throw new InvalidOperationException("Cannot re-call start of asynchronous " +
                                                    "method while a previous call is still in progress.");
            }

            _asyncRead = new TaskCompletionSource<HttpWebResponse>();

            var aread = _asyncRead;
            _initialMethod = _method;

            var writeTask = CheckIfForceWriteAsync();

            if (null == writeTask)
                GetResponseAsyncCB2(aread);
            else
            {
                writeTask.ContinueWith(t =>
                                       {
                                           var ex = t.Exception;

                                           if (null != ex)
                                               aread.TrySetException(ex);
                                           else
                                               GetResponseAsyncCB2(aread);
                                       }, cancellationToken);

                Monitor.Exit(_locker);
            }

            //aread.InnerAsyncResult = CheckIfForceWrite(GetResponseAsyncCB, aread);
            //if (aread.InnerAsyncResult == null)
            //    GetResponseAsyncCB2(aread);
            //else
            //    Monitor.Exit(locker);

            return aread.Task;
        }

        //void GetResponseAsyncCB(IAsyncResult ar)
        //{
        //    var result = (WebAsyncResult)ar;
        //    var innerResult = (WebAsyncResult)result.InnerAsyncResult;
        //    result.InnerAsyncResult = null;

        //    if (innerResult != null && innerResult.GotException)
        //    {
        //        _asyncRead.TrySetException(innerResult.Exception);
        //        //asyncRead.SetCompleted(true, innerResult.Exception);
        //        //asyncRead.DoCallback();
        //        return;
        //    }

        //    Monitor.Enter(_locker);
        //    GetResponseAsyncCB2((WebAsyncResult)ar);
        //}

        void GetResponseAsyncCB2(TaskCompletionSource<HttpWebResponse> aread)
        {
            if (_haveResponse)
            {
                var saved = _savedExc;
                if (_webResponse != null)
                {
                    Monitor.Exit(_locker);
                    if (saved == null)
                        aread.TrySetResult(_webResponse);
                    else
                        aread.TrySetException(saved);
                    return;
                }

                if (saved != null)
                {
                    Monitor.Exit(_locker);
                    aread.TrySetException(saved);
                    return;
                }
            }

            if (!_requestSent)
            {
                _requestSent = true;
                _redirects = 0;
                _servicePoint = GetServicePoint();
                _abortHandler = _servicePoint.SendRequest(this, _connectionGroup);
            }

            Monitor.Exit(_locker);
        }

        //public override WebResponse EndGetResponse(IAsyncResult asyncResult)
        //{
        //    if (asyncResult == null)
        //        throw new ArgumentNullException("asyncResult");

        //    var result = asyncResult as WebAsyncResult;
        //    if (result == null)
        //        throw new ArgumentException("Invalid IAsyncResult", "asyncResult");

        //    if (!result.WaitUntilComplete(timeout, false))
        //    {
        //        Abort();
        //        throw new WebException("The request timed out", WebExceptionStatus.Timeout);
        //    }

        //    if (result.GotException)
        //        throw result.Exception;

        //    return result.Response;
        //}

        internal bool FinishedReading { get; set; }

        internal bool Aborted
        {
            get { return Interlocked.CompareExchange(ref _aborted, 0, 0) == 1; }
        }

        public override void Abort()
        {
            if (Interlocked.CompareExchange(ref _aborted, 1, 0) == 1)
                return;

            if (_haveResponse && FinishedReading)
                return;

            _haveResponse = true;
            if (_abortHandler != null)
            {
                try
                {
                    _abortHandler(this, EventArgs.Empty);
                }
                catch (Exception)
                { }
                _abortHandler = null;
            }

            if (_asyncWrite != null)
            {
                var r = _asyncWrite;
                if (!r.Task.IsCompleted)
                {
                    try
                    {
                        var wexc = new WebException("Aborted.", WebExceptionStatus.RequestCanceled);
                        r.TrySetException(wexc);
                    }
                    catch
                    { }
                }

                _asyncWrite = null;
            }

            if (_asyncRead != null)
            {
                var r = _asyncRead;
                if (!r.Task.IsCompleted)
                {
                    try
                    {
                        var wexc = new WebException("Aborted.", WebExceptionStatus.RequestCanceled);
                        r.TrySetException(wexc);
                    }
                    catch
                    { }
                }

                _asyncRead = null;
            }

            if (_writeStream != null)
            {
                try
                {
                    _writeStream.Close();
                    _writeStream = null;
                }
                catch
                { }
            }

            if (_webResponse != null)
            {
                try
                {
                    _webResponse.Close();
                    _webResponse = null;
                }
                catch
                { }
            }
        }

        void CheckRequestStarted()
        {
            if (_requestSent)
                throw new InvalidOperationException("request started");
        }

        internal void DoContinueDelegate(int statusCode, WebHeaderCollection headers)
        {
            if (ContinueDelegate != null)
                ContinueDelegate(statusCode, headers);
        }

        bool Redirect(HttpStatusCode code)
        {
            _redirects++;
            Exception e = null;
            string uriString = null;
            switch (code)
            {
                case HttpStatusCode.Ambiguous: // 300
                    e = new WebException("Ambiguous redirect.");
                    break;
                case HttpStatusCode.MovedPermanently: // 301
                case HttpStatusCode.Redirect: // 302
                    if (_method == "POST")
                        _method = "GET";
                    break;
                case HttpStatusCode.TemporaryRedirect: // 307
                    break;
                case HttpStatusCode.SeeOther: //303
                    _method = "GET";
                    break;
                case HttpStatusCode.NotModified: // 304
                    return false;
                case HttpStatusCode.UseProxy: // 305
                    e = new NotImplementedException("Proxy support not available.");
                    break;
                case HttpStatusCode.Unused: // 306
                default:
                    e = new ProtocolViolationException("Invalid status code: " + (int)code);
                    break;
            }

            if (e != null)
                throw e;

            _contentLength = -1;
            //bodyBufferLength = 0;
            //bodyBuffer = null;
            uriString = _webResponse.Headers["Location"];

            if (uriString == null)
            {
                throw new WebException("No Location header found for " + (int)code,
                    WebExceptionStatus.ProtocolError);
            }

            var prev = Address;
            try
            {
                Address = new Uri(Address, uriString);
            }
            catch (Exception)
            {
                throw new WebException(String.Format("Invalid URL ({0}) for {1}",
                    uriString, (int)code),
                    WebExceptionStatus.ProtocolError);
            }

            _hostChanged = (Address.Scheme != prev.Scheme || Host != prev.Authority);

            return true;
        }

        string GetHeaders()
        {
            var continue100 = false;
            if (_sendChunked)
            {
                continue100 = true;
                _webHeaders.RemoveAndAdd("Transfer-Encoding", "chunked");
                _webHeaders.RemoveInternal("Content-Length");
            }
            else if (_contentLength != -1)
            {
                if (_authState.NtlmAuthState == NtlmAuthState.Challenge || _proxyAuthState.NtlmAuthState == NtlmAuthState.Challenge)
                {
                    // We don't send any body with the NTLM Challenge request.
                    _webHeaders.SetInternal("Content-Length", "0");
                }
                else
                {
                    if (_contentLength > 0)
                        continue100 = true;

                    if (_gotRequestStream || _contentLength > 0)
                        _webHeaders.SetInternal("Content-Length", _contentLength.ToString());
                }
                _webHeaders.RemoveInternal("Transfer-Encoding");
            }
            else
                _webHeaders.RemoveInternal("Content-Length");

            if (_actualVersion == HttpVersion.Version11 && continue100 &&
                _servicePoint.SendContinue)
            {
                // RFC2616 8.2.3
                _webHeaders.RemoveAndAdd("Expect", "100-continue");
                ExpectContinue = true;
            }
            else
            {
                _webHeaders.RemoveInternal("Expect");
                ExpectContinue = false;
            }

            var proxy_query = ProxyQuery;
            var connectionHeader = (proxy_query) ? "Proxy-Connection" : "Connection";
            _webHeaders.RemoveInternal((!proxy_query) ? "Proxy-Connection" : "Connection");
            var proto_version = _servicePoint.ProtocolVersion;
            var spoint10 = (proto_version == null || proto_version == HttpVersion.Version10);

            if (KeepAlive && (_version == HttpVersion.Version10 || spoint10))
            {
                if (_webHeaders[connectionHeader] == null
                    || _webHeaders[connectionHeader].IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) == -1)
                    _webHeaders.RemoveAndAdd(connectionHeader, "keep-alive");
            }
            else if (!KeepAlive && _version == HttpVersion.Version11)
                _webHeaders.RemoveAndAdd(connectionHeader, "close");

            _webHeaders.SetInternal("Host", Host);
            if (CookieContainer != null)
            {
                var cookieHeader = CookieContainer.GetCookieHeader(Address);
                if (cookieHeader != "")
                    _webHeaders.RemoveAndAdd("Cookie", cookieHeader);
                else
                    _webHeaders.RemoveInternal("Cookie");
            }

            string accept_encoding = null;
            if ((_autoDecomp & DecompressionMethods.GZip) != 0)
                accept_encoding = "gzip";
            if ((_autoDecomp & DecompressionMethods.Deflate) != 0)
                accept_encoding = accept_encoding != null ? "gzip, deflate" : "deflate";
            if (accept_encoding != null)
                _webHeaders.RemoveAndAdd("Accept-Encoding", accept_encoding);

            if (!_usedPreAuth && _preAuthenticate)
                DoPreAuthenticate();

            return _webHeaders.ToString();
        }

        void DoPreAuthenticate()
        {
            var isProxy = (_proxy != null && !_proxy.IsBypassed(Address));
            var creds = (!isProxy || _credentials != null) ? _credentials : _proxy.Credentials;
            var auth = AuthenticationManager.PreAuthenticate(this, creds);
            if (auth == null)
                return;

            _webHeaders.RemoveInternal("Proxy-Authorization");
            _webHeaders.RemoveInternal("Authorization");
            var authHeader = (isProxy && _credentials == null) ? "Proxy-Authorization" : "Authorization";
            _webHeaders[authHeader] = auth.Message;
            _usedPreAuth = true;
        }

        internal void SetWriteStreamError(WebExceptionStatus status, Exception exc)
        {
            if (Aborted)
                return;

            if (null != _asyncWrite || null != _asyncRead)
            {
                string msg;
                WebException wex;
                if (exc == null)
                {
                    msg = "Error: " + status;
                    wex = new WebException(msg, status);
                }
                else
                {
                    msg = String.Format("Error: {0} ({1})", status, exc.Message);
                    wex = new WebException(msg, exc, status);
                }

                if (null != _asyncWrite)
                    _asyncWrite.TrySetException(wex);
                if (null != _asyncRead)
                    _asyncRead.TrySetException(wex);
            }
        }

        internal byte[] GetRequestHeaders()
        {
            var req = new StringBuilder();
            string query;
            if (!ProxyQuery)
                query = Address.PathAndQuery;
            else
            {
                query = String.Format("{0}://{1}{2}", Address.Scheme,
                    Host,
                    Address.PathAndQuery);
            }

            if (!_forceVersion && _servicePoint.ProtocolVersion != null && _servicePoint.ProtocolVersion < _version)
                _actualVersion = _servicePoint.ProtocolVersion;
            else
                _actualVersion = _version;

            req.AppendFormat("{0} {1} HTTP/{2}.{3}\r\n", _method, query,
                _actualVersion.Major, _actualVersion.Minor);
            req.Append(GetHeaders());
            var reqstr = req.ToString();
            return Encoding.UTF8.GetBytes(reqstr);
        }

        internal async Task SetWriteStreamAsync(WebConnectionStream stream)
        {
            if (Aborted)
                return;

            _writeStream = stream;
            if (_bodyBuffer != null)
            {
                _webHeaders.RemoveInternal("Transfer-Encoding");
                _contentLength = _bodyBufferLength;
                _writeStream.SendChunked = false;
            }

            try
            {
                await _writeStream.SetHeadersAsync(false).ConfigureAwait(false);

                await SetWriteStreamCB().ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                SetWriteStreamErrorCB(exc);
            }
        }

        void SetWriteStreamErrorCB(Exception exc)
        {
            var wexc = exc as WebException;
            if (wexc != null)
                SetWriteStreamError(wexc.Status, wexc);
            else
                SetWriteStreamError(WebExceptionStatus.SendFailure, exc);
        }

        async Task SetWriteStreamCB()
        {
            //var result = ar as WebAsyncResult;

            //if (result != null && result.Exception != null)
            //{
            //    SetWriteStreamErrorCB(result.Exception);
            //    return;
            //}

            _haveRequest = true;

            //WebAsyncResult writeRequestResult = null;

            if (_bodyBuffer != null)
            {
                // The body has been written and buffered. The request "user"
                // won't write it again, so we must do it.
                if (_authState.NtlmAuthState != NtlmAuthState.Challenge && _proxyAuthState.NtlmAuthState != NtlmAuthState.Challenge)
                {
                    await _writeStream.WriteAsync(_bodyBuffer, 0, _bodyBufferLength).ConfigureAwait(false);

                    _bodyBuffer = null;

                    await _writeStream.CloseAsync().ConfigureAwait(false);
                }
            }
            else if (_method != "HEAD" && _method != "GET" && _method != "MKCOL" && _method != "CONNECT" &&
                     _method != "TRACE")
            {
                if (_getResponseCalled && !_writeStream.RequestWritten)
                    await _writeStream.WriteRequestAsync().ConfigureAwait(false);
            }

            if (_asyncWrite != null)
            {
                _asyncWrite.TrySetResult(_writeStream);
                _asyncWrite = null;
            }
        }

        //void SetWriteStreamCB2(IAsyncResult ar)
        //{
        //    var result = (WebAsyncResult)ar;
        //    if (result != null && result.GotException)
        //    {
        //        SetWriteStreamErrorCB(result.Exception);
        //        return;
        //    }

        //    if (_asyncWrite != null)
        //    {
        //        _asyncWrite.TrySetResult(_writeStream);
        //        _asyncWrite = null;
        //    }
        //}

        internal void SetResponseError(WebExceptionStatus status, Exception e, string where)
        {
            if (Aborted)
                return;
            lock (_locker)
            {
                var msg = String.Format("Error getting response stream ({0}): {1}", where, status);

                var wexc = e as WebException ?? new WebException(msg, e, status, null);

                if (null != _asyncWrite || null != _asyncRead)
                {
                    if (null != _asyncRead && !_asyncRead.Task.IsCompleted)
                        _asyncRead.TrySetException(wexc);

                    if (null != _asyncWrite && !_asyncWrite.Task.IsCompleted)
                        _asyncWrite.TrySetException(wexc);

                    if (null == _asyncRead)
                        _savedExc = wexc;

                    _haveResponse = true;
                    _asyncRead = null;
                    _asyncWrite = null;
                }
                else
                {
                    _haveResponse = true;
                    _savedExc = wexc;
                }
            }
        }

        async Task CheckSendErrorAsync(WebConnectionData data)
        {
            // Got here, but no one called GetResponse
            var status = data.StatusCode;
            if (status < 400 || status == 401 || status == 407)
                return;

            if (_writeStream != null && _asyncRead == null && !_writeStream.CompleteRequestWritten)
            {
                // The request has not been completely sent and we got here!
                // We should probably just close and cause an error in any case,
                _savedExc = new WebException(data.StatusDescription, null, WebExceptionStatus.ProtocolError, _webResponse);
                if (AllowWriteStreamBuffering || _sendChunked || _writeStream.TotalWritten >= _contentLength)
                    await _webResponse.ReadAllAsync().ConfigureAwait(false);
                else
                    _writeStream.IgnoreIoErrors = true;
            }
        }

        async Task<bool> HandleNtlmAuthAsync(Task<HttpWebResponse> r)
        {
            var isProxy = _webResponse.StatusCode == HttpStatusCode.ProxyAuthenticationRequired;
            if ((isProxy ? _proxyAuthState.NtlmAuthState : _authState.NtlmAuthState) == NtlmAuthState.None)
                return false;

            var wce = _webResponse.GetResponseStream() as WebConnectionStream;
            if (wce != null)
            {
                var cnc = wce.Connection;
                cnc.PriorityRequest = this;
                var creds = !isProxy ? _credentials : _proxy.Credentials;
                if (creds != null)
                {
                    cnc.NtlmCredential = creds.GetCredential(_requestUri, "NTLM");
                    cnc.UnsafeAuthenticatedConnectionSharing = UnsafeAuthenticatedConnectionSharing;
                }
            }
            //r.Reset();
            FinishedReading = false;
            _haveResponse = false;
            await _webResponse.ReadAllAsync().ConfigureAwait(false);
            _webResponse = null;
            return true;
        }

        internal async Task SetResponseDataAsync(WebConnectionData data)
        {
            var isLocked = false;
            var closeStream = default(Stream);

            try
            {
                Monitor.Enter(_locker, ref isLocked);

                if (Aborted)
                {
                    closeStream = data.Stream;

                    return;
                }

                _previousWebResponse = _webResponse;

                WebException wexc = null;
                try
                {
                    var createTask = HttpWebResponse.CreateAsync(Address, _method, data, CookieContainer);

                    if (createTask.IsCompleted)
                        _webResponse = createTask.Result;
                    else
                    {
                        try
                        {
                            Monitor.Exit(_locker);
                            isLocked = false;

                            _webResponse = await createTask.ConfigureAwait(false);
                        }
                        finally
                        {
                            Monitor.Enter(_locker, ref isLocked);
                        }
                    }
                }
                catch (Exception e)
                {
                    wexc = new WebException(e.Message, e, WebExceptionStatus.ProtocolError, null);
                    closeStream = data.Stream;
                }

                if (wexc == null && (_method == "POST" || _method == "PUT"))
                {
                    await CheckSendErrorAsync(data).ConfigureAwait(false);
                    if (_savedExc != null)
                        wexc = (WebException)_savedExc;
                }
            }
            finally
            {
                if (isLocked)
                    Monitor.Exit(_locker);

                if (null != closeStream)
                    closeStream.Close();
            }

            isLocked = false;
            try
            {
                Monitor.TryEnter(_locker, ref isLocked);

                //if (Aborted)
                //{
                //    if (data.Stream != null)
                //        data.Stream.Close();
                //    return;
                //}

                WebException wexc = null;
                //try
                //{
                //    webResponse = await HttpWebResponse.CreateAsync(Address, method, data, CookieContainer).ConfigureAwait(false);
                //}
                //catch (Exception e)
                //{
                //    wexc = new WebException(e.Message, e, WebExceptionStatus.ProtocolError, null);
                //    if (data.Stream != null)
                //        data.Stream.Close();
                //}

                //if (wexc == null && (method == "POST" || method == "PUT"))
                //{
                //    CheckSendError(data);
                //    if (saved_exc != null)
                //        wexc = (WebException)saved_exc;
                //}

                var r = _asyncRead;

                var forced = false;
                if (r == null && _webResponse != null)
                {
                    // This is a forced completion (302, 204)...
                    forced = true;
                    r = new TaskCompletionSource<HttpWebResponse>();
                    r.TrySetResult(_webResponse);
                }

                if (r != null)
                {
                    if (wexc != null)
                    {
                        _haveResponse = true;
                        if (!r.Task.IsCompleted)
                            r.TrySetException(wexc);
                        return;
                    }

                    var isProxy = ProxyQuery && !_proxy.IsBypassed(Address);

                    try
                    {
                        var redirected = await CheckFinalStatusAsync().ConfigureAwait(false);

                        if (!redirected)
                        {
                            if ((isProxy ? _proxyAuthState.IsNtlmAuthenticated : _authState.IsNtlmAuthenticated) &&
                                _webResponse != null && (int)_webResponse.StatusCode < 400)
                            {
                                var wce = _webResponse.GetResponseStream() as WebConnectionStream;
                                if (wce != null)
                                {
                                    var cnc = wce.Connection;
                                    cnc.NtlmAuthenticated = true;
                                }
                            }

                            // clear internal buffer so that it does not
                            // hold possible big buffer (bug #397627)
                            if (_writeStream != null)
                                _writeStream.KillBuffer();

                            _haveResponse = true;
                            r.TrySetResult(_webResponse);
                        }
                        else
                        {
                            if (_webResponse != null)
                            {
                                if (await HandleNtlmAuthAsync(r.Task).ConfigureAwait(false))
                                    return;
                                _webResponse.Close();
                            }
                            FinishedReading = false;
                            _haveResponse = false;
                            _webResponse = null;
                            //r.Reset();
                            _servicePoint = GetServicePoint();
                            _abortHandler = _servicePoint.SendRequest(this, _connectionGroup);
                        }
                    }
                    catch (WebException wexc2)
                    {
                        if (forced)
                        {
                            _savedExc = wexc2;
                            _haveResponse = true;
                        }
                        r.TrySetException(wexc2);
                    }
                    catch (Exception ex)
                    {
                        wexc = new WebException(ex.Message, ex, WebExceptionStatus.ProtocolError, null);
                        if (forced)
                        {
                            _savedExc = wexc;
                            _haveResponse = true;
                        }
                        r.TrySetException(wexc);
                    }
                }
            }
            finally
            {
                if (isLocked)
                    Monitor.Exit(_locker);
            }
        }

        struct AuthorizationState
        {
            readonly bool isProxy;
            readonly HttpWebRequest request;
            bool isCompleted;
            NtlmAuthState ntlm_auth_state;

            public AuthorizationState(HttpWebRequest request, bool isProxy)
            {
                this.request = request;
                this.isProxy = isProxy;
                isCompleted = false;
                ntlm_auth_state = NtlmAuthState.None;
            }

            public bool IsCompleted
            {
                get { return isCompleted; }
            }

            public NtlmAuthState NtlmAuthState
            {
                get { return ntlm_auth_state; }
            }

            public bool IsNtlmAuthenticated
            {
                get { return isCompleted && ntlm_auth_state != NtlmAuthState.None; }
            }

            public bool CheckAuthorization(WebResponse response, HttpStatusCode code)
            {
                isCompleted = false;
                if (code == HttpStatusCode.Unauthorized && request._credentials == null)
                    return false;

                // FIXME: This should never happen!
                if (isProxy != (code == HttpStatusCode.ProxyAuthenticationRequired))
                    return false;

                if (isProxy && (request._proxy == null || request._proxy.Credentials == null))
                    return false;

                var authHeaders = response.Headers.GetValues_internal(isProxy ? "Proxy-Authenticate" : "WWW-Authenticate", false);
                if (authHeaders == null || authHeaders.Length == 0)
                    return false;

                var creds = (!isProxy) ? request._credentials : request._proxy.Credentials;
                Authorization auth = null;
                foreach (var authHeader in authHeaders)
                {
                    auth = AuthenticationManager.Authenticate(authHeader, request, creds);
                    if (auth != null)
                        break;
                }
                if (auth == null)
                    return false;
                request._webHeaders[isProxy ? "Proxy-Authorization" : "Authorization"] = auth.Message;
                isCompleted = auth.Complete;
                var is_ntlm = (auth.Module.AuthenticationType == "NTLM");
                if (is_ntlm)
                    ntlm_auth_state = (NtlmAuthState)((int)ntlm_auth_state + 1);
                return true;
            }

            public void Reset()
            {
                isCompleted = false;
                ntlm_auth_state = NtlmAuthState.None;
                request._webHeaders.RemoveInternal(isProxy ? "Proxy-Authorization" : "Authorization");
            }

            public override string ToString()
            {
                return string.Format("{0}AuthState [{1}:{2}]", isProxy ? "Proxy" : "", isCompleted, ntlm_auth_state);
            }
        }

        bool CheckAuthorization(WebResponse response, HttpStatusCode code)
        {
            var isProxy = code == HttpStatusCode.ProxyAuthenticationRequired;
            return isProxy ? _proxyAuthState.CheckAuthorization(response, code) : _authState.CheckAuthorization(response, code);
        }

        // Returns true if redirected
        async Task<bool> CheckFinalStatusAsync()
        {
            //if (result.IsFaulted)
            //{
            //    _bodyBuffer = null;

            //    throw result.Exception;
            //}

            Exception throwMe = null;

            //var resp = result.Result;
            var protoError = WebExceptionStatus.ProtocolError;
            HttpStatusCode code = 0;
            if (throwMe == null && _webResponse != null)
            {
                code = _webResponse.StatusCode;
                if ((!_authState.IsCompleted && code == HttpStatusCode.Unauthorized && _credentials != null) ||
                    (ProxyQuery && !_proxyAuthState.IsCompleted && code == HttpStatusCode.ProxyAuthenticationRequired))
                {
                    if (!_usedPreAuth && CheckAuthorization(_webResponse, code))
                    {
                        // Keep the written body, so it can be rewritten in the retry
                        if (InternalAllowBuffering)
                        {
                            // NTLM: This is to avoid sending data in the 'challenge' request
                            // We save it in the first request (first 401), don't send anything
                            // in the challenge request and send it in the response request along
                            // with the buffers kept form the first request.
                            if (_authState.NtlmAuthState == NtlmAuthState.Challenge || _proxyAuthState.NtlmAuthState == NtlmAuthState.Challenge)
                            {
                                _bodyBuffer = _writeStream.WriteBuffer;
                                _bodyBufferLength = _writeStream.WriteBufferLength;
                            }
                            return true;
                        }
                        if (_method != "PUT" && _method != "POST")
                        {
                            _bodyBuffer = null;
                            return true;
                        }

                        if (!ThrowOnError)
                            return false;

                        _writeStream.InternalClose();
                        _writeStream = null;
                        _webResponse.Close();
                        _webResponse = null;
                        _bodyBuffer = null;

                        throw new WebException("This request requires buffering " +
                                               "of data for authentication or " +
                                               "redirection to be sucessful.");
                    }
                }

                _bodyBuffer = null;
                if ((int)code >= 400)
                {
                    var err = String.Format("The remote server returned an error: ({0}) {1}.",
                        (int)code, _webResponse.StatusDescription);
                    throwMe = new WebException(err, null, protoError, _webResponse);
                    await _webResponse.ReadAllAsync().ConfigureAwait(false);
                }
                else if ((int)code == 304 && AllowAutoRedirect)
                {
                    var err = String.Format("The remote server returned an error: ({0}) {1}.",
                        (int)code, _webResponse.StatusDescription);
                    throwMe = new WebException(err, null, protoError, _webResponse);
                }
                else if ((int)code >= 300 && AllowAutoRedirect && _redirects >= _maxAutoRedirect)
                {
                    throwMe = new WebException("Max. redirections exceeded.", null,
                        protoError, _webResponse);
                    await _webResponse.ReadAllAsync().ConfigureAwait(false);
                }
            }

            _bodyBuffer = null;
            if (throwMe == null)
            {
                var b = false;
                var c = (int)code;
                if (AllowAutoRedirect && c >= 300)
                {
                    b = Redirect(code);
                    if (InternalAllowBuffering && _writeStream.WriteBufferLength > 0)
                    {
                        _bodyBuffer = _writeStream.WriteBuffer;
                        _bodyBufferLength = _writeStream.WriteBufferLength;
                    }
                    if (b && !UnsafeAuthenticatedConnectionSharing)
                    {
                        _authState.Reset();
                        _proxyAuthState.Reset();
                    }
                }

                if (_previousWebResponse != null && c >= 300 && c != 304)
                    await _previousWebResponse.ReadAllAsync().ConfigureAwait(false);

                return b;
            }

            if (!ThrowOnError)
                return false;

            if (_writeStream != null)
            {
                _writeStream.InternalClose();
                _writeStream = null;
            }

            _webResponse = null;

            throw throwMe;
        }
    }
}
