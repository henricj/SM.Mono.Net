//
// System.Net.WebConnectionData
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
using System.Net;

namespace SM.Mono.Net
{
    class WebConnectionData
    {
        public string[] Challenge;
        public WebHeaderCollection Headers;
        public int StatusCode;
        public string StatusDescription;
        public WebConnectionStream Stream;
        public Version Version;
        ReadState _readState;

        public WebConnectionData()
        {
            _readState = ReadState.None;
        }

        public WebConnectionData(HttpWebRequest request)
        {
            Request = request;
            //_readState = ???;
        }

        public HttpWebRequest Request { get; set; }

        public ReadState ReadState
        {
            get { return _readState; }
            set
            {
                lock (this)
                {
                    if ((_readState == ReadState.Aborted) && (value != ReadState.Aborted))
                        throw new WebException("Aborted", WebExceptionStatus.RequestCanceled);
                    _readState = value;
                }
            }
        }
    }
}
