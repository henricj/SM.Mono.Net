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
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SM.Mono.Utility
{
    public static class SocketExtensions
    {
        public static Task ConnectAsync(this Socket socket, IPEndPoint endPoint)
        {
            var tcs = new TaskCompletionSource<object>();

            var ea = new SocketAsyncEventArgs
                     {
                         RemoteEndPoint = endPoint
                     };

            EventHandler<SocketAsyncEventArgs> completedHandler = null;

            completedHandler = (sender, args) =>
                               {
                                   switch (args.SocketError)
                                   {
                                       case SocketError.ConnectionAborted:
                                       case SocketError.OperationAborted:
                                           tcs.TrySetCanceled();
                                           break;
                                       case SocketError.Success:
                                           tcs.TrySetResult(string.Empty);
                                           break;
                                       default:
                                           tcs.TrySetException(new SocketException((int)args.SocketError));
                                           break;
                                   }

                                   ea.Completed -= completedHandler;
                               };

            ea.Completed += completedHandler;

            socket.ConnectAsync(ea);

            return tcs.Task;
        }
    }
}
