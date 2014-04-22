//
// System.Net.HttpWebResponse
//
// Authors:
// 	Lawrence Pit (loz@cable.a2000.nl)
// 	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//      Daniel Nauck    (dna(at)mono-project(dot)de)
//
// (c) 2002 Lawrence Pit
// (c) 2003 Ximian, Inc. (http://www.ximian.com)
// (c) 2008 Daniel Nauck
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

//using System.IO.Compression;

using System;
using System.IO;
using System.Threading.Tasks;

namespace SM.Mono.Net
{
    public class HttpWebResponse : WebResponse, IDisposable
    {
        readonly Uri _uri;
        readonly WebHeaderCollection _webHeaders;
        CookieCollection _cookieCollection;
        readonly string _method;
        readonly Version _version;
        readonly HttpStatusCode _statusCode;
        readonly string _statusDescription;
        readonly long _contentLength;
        string _contentType;
        readonly CookieContainer _cookieContainer;

        bool _disposed;
        WebConnectionStream _stream;

        // Constructors

        HttpWebResponse(Uri uri, string method, WebConnectionData data, CookieContainer container)
        {
            _uri = uri;
            _method = method;
            _webHeaders = data.Headers;
            _version = data.Version;
            _statusCode = (HttpStatusCode)data.StatusCode;
            _statusDescription = data.StatusDescription;
            _stream = data.Stream;
            _contentLength = -1;

            try
            {
                var cl = _webHeaders["Content-Length"];
                if (String.IsNullOrEmpty(cl) || !Int64.TryParse(cl, out _contentLength))
                    _contentLength = -1;
            }
            catch (Exception)
            {
                _contentLength = -1;
            }

            if (container != null)
            {
                _cookieContainer = container;
                //FillCookiesAsync();
            }

#if false
            string content_encoding = webHeaders ["Content-Encoding"];
            if (content_encoding == "gzip" && (data.request.AutomaticDecompression & DecompressionMethods.GZip) != 0)
                stream = new GZipStream (stream, CompressionMode.Decompress);
            else if (content_encoding == "deflate" && (data.request.AutomaticDecompression & DecompressionMethods.Deflate) != 0)
                stream = new DeflateStream (stream, CompressionMode.Decompress);
#endif
        }

        internal static async Task<HttpWebResponse> CreateAsync(Uri uri, string method, WebConnectionData data, CookieContainer container)
        {
            var response = new HttpWebResponse(uri, method, data, container);

            if (null != container)
                await response.FillCookiesAsync().ConfigureAwait(false);

            return response;
        }

        //[Obsolete ("Serialization is obsoleted for this type", false)]
        //protected HttpWebResponse (SerializationInfo serializationInfo, StreamingContext streamingContext)
        //{
        //    SerializationInfo info = serializationInfo;

        //    uri = (Uri) info.GetValue ("uri", typeof (Uri));
        //    contentLength = info.GetInt64 ("contentLength");
        //    contentType = info.GetString ("contentType");
        //    method = info.GetString ("method");
        //    statusDescription = info.GetString ("statusDescription");
        //    cookieCollection = (CookieCollection) info.GetValue ("cookieCollection", typeof (CookieCollection));
        //    version = (Version) info.GetValue ("version", typeof (Version));
        //    statusCode = (HttpStatusCode) info.GetValue ("statusCode", typeof (HttpStatusCode));
        //}

        // Properties

        public string CharacterSet
        {
            // Content-Type   = "Content-Type" ":" media-type
            // media-type     = type "/" subtype *( ";" parameter )
            // parameter      = attribute "=" value
            // 3.7.1. default is ISO-8859-1
            get
            {
                var contentType = ContentType;
                if (contentType == null)
                    return "ISO-8859-1";
                var val = contentType.ToLower();
                var pos = val.IndexOf("charset=", StringComparison.Ordinal);
                if (pos == -1)
                    return "ISO-8859-1";
                pos += 8;
                var pos2 = val.IndexOf(';', pos);
                return (pos2 == -1)
                    ? contentType.Substring(pos)
                    : contentType.Substring(pos, pos2 - pos);
            }
        }

        public string ContentEncoding
        {
            get
            {
                CheckDisposed();
                var h = _webHeaders["Content-Encoding"];
                return h ?? "";
            }
        }

        public override long ContentLength
        {
            get { return _contentLength; }
        }

        public override string ContentType
        {
            get
            {
                CheckDisposed();

                if (_contentType == null)
                    _contentType = _webHeaders["Content-Type"];

                return _contentType;
            }
        }

#if NET_4_5
        virtual
#endif

        public CookieCollection Cookies
        {
            get
            {
                CheckDisposed();
                if (_cookieCollection == null)
                    _cookieCollection = new CookieCollection();
                return _cookieCollection;
            }
            set
            {
                CheckDisposed();
                _cookieCollection = value;
            }
        }

        public override WebHeaderCollection Headers
        {
            get { return _webHeaders; }
        }

        static Exception GetMustImplement()
        {
            return new NotImplementedException();
        }

        [MonoTODO]
        public override bool IsMutuallyAuthenticated
        {
            get { throw GetMustImplement(); }
        }

        public DateTime LastModified
        {
            get
            {
                CheckDisposed();
                try
                {
                    var dtStr = _webHeaders["Last-Modified"];
                    return MonoHttpDate.Parse(dtStr);
                }
                catch (Exception)
                {
                    return DateTime.Now;
                }
            }
        }

#if NET_4_5
        virtual
#endif

        public string Method
        {
            get
            {
                CheckDisposed();
                return _method;
            }
        }

        public Version ProtocolVersion
        {
            get
            {
                CheckDisposed();
                return _version;
            }
        }

        public override Uri ResponseUri
        {
            get
            {
                CheckDisposed();
                return _uri;
            }
        }

        public string Server
        {
            get
            {
                CheckDisposed();
                return _webHeaders["Server"];
            }
        }

#if NET_4_5
        virtual
#endif

        public HttpStatusCode StatusCode
        {
            get { return _statusCode; }
        }

#if NET_4_5
        virtual
#endif

        public string StatusDescription
        {
            get
            {
                CheckDisposed();
                return _statusDescription;
            }
        }

        // Methods

        public string GetResponseHeader(string headerName)
        {
            CheckDisposed();
            var value = _webHeaders[headerName];
            return value ?? "";
        }

        async internal Task ReadAllAsync()
        {
            var wce = _stream;
            if (wce == null)
                return;

            try
            {
                await wce.ReadAllAsync().ConfigureAwait(false);
            }
            catch
            { }
        }

        public override Stream GetResponseStream()
        {
            CheckDisposed();
            if (_stream == null)
                return Stream.Null;
            if (string.Equals(_method, "HEAD", StringComparison.OrdinalIgnoreCase)) // see par 4.3 & 9.4
                return Stream.Null;

            return _stream;
        }

        //void ISerializable.GetObjectData (SerializationInfo serializationInfo,
        //                  StreamingContext streamingContext)
        //{
        //    GetObjectData (serializationInfo, streamingContext);
        //}

        //protected override void GetObjectData (SerializationInfo serializationInfo,
        //    StreamingContext streamingContext)
        //{
        //    SerializationInfo info = serializationInfo;

        //    info.AddValue ("uri", uri);
        //    info.AddValue ("contentLength", contentLength);
        //    info.AddValue ("contentType", contentType);
        //    info.AddValue ("method", method);
        //    info.AddValue ("statusDescription", statusDescription);
        //    info.AddValue ("cookieCollection", cookieCollection);
        //    info.AddValue ("version", version);
        //    info.AddValue ("statusCode", statusCode);
        //}

        // Cleaning up stuff

        public override void Close()
        {
            if (_stream != null)
            {
                var st = _stream;
                _stream = null;
                if (st != null)
                    st.Close();
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
        }

#if NET_4_0
        protected override void Dispose (bool disposing)
        {
            this.disposed = true;
            base.Dispose (true);
        }
#else
        void Dispose(bool disposing)
        {
            _disposed = true;
            if (disposing)
                Close();
        }
#endif

        void CheckDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        async Task FillCookiesAsync()
        {
            if (_webHeaders == null)
                return;

            var value = _webHeaders.Get("Set-Cookie");
            if (value != null)
                await SetCookieAsync(value).ConfigureAwait(false);

            value = _webHeaders.Get("Set-Cookie2");
            if (value != null)
                await SetCookieAsync(value).ConfigureAwait(false);
        }

        async Task SetCookieAsync(string header)
        {
            if (_cookieCollection == null)
                _cookieCollection = new CookieCollection();

            var parser = new CookieParser(header);
            foreach (var cookie in parser.Parse())
            {
                if (cookie.Domain == "")
                {
                    cookie.Domain = _uri.Host;
                    cookie.HasDomain = false;
                }

                if (cookie.HasDomain &&
                    !await CookieContainer.CheckSameOriginAsync(_uri, cookie.Domain).ConfigureAwait(false))
                    continue;

                _cookieCollection.Add(cookie);
                if (_cookieContainer != null)
                    _cookieContainer.AddAsync(_uri, cookie);
            }
        }
    }
}
