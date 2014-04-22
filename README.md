SM.Mono.Net
===========

Port of Mono's HttpWebRequest to Windows Phone.

The default implementations of HttpClient and HttpWebRequest can sometimes cause out
of memory exceptions and the cache can fill the phone's flash.  This implementation uses
StreamSocket directly, thereby bypassing the platform's IXMLHTTPRequest.

No attempt was made to preserve the HttpWebRequest API. Much of the code was converted
from APM to TPL.

At this point, only a few GET requests have been tried.  TLS does not work.
