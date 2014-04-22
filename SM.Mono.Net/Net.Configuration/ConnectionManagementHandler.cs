//
// System.Net.Configuration.ConnectionManagementHandler
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
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using SM.Mono.Collections;
#if (XML_DEP)
using System.Xml;
#else
using XmlNode = System.Object;
#endif

namespace SM.Mono.Net.Configuration
{
	class ConnectionManagementData
	{
		Dictionary<string, uint> data; // key -> address, value -> maxconnections
		const int defaultMaxConnections = 2;
		
		public ConnectionManagementData (object parent)
		{
            var p = parent as ConnectionManagementData;

		    if (null == p)
		        data = new Dictionary<string, uint>(StringComparer.InvariantCultureIgnoreCase);
		    else
		        data = new Dictionary<string, uint>(p.data, StringComparer.InvariantCultureIgnoreCase);
		}

		public void Add (string address, string nconns)
		{
			if (string.IsNullOrEmpty(nconns))
				nconns = "2";
			// Adding duplicates works fine under MS, so...
			data [address] = UInt32.Parse (nconns);
		}

		public void Add (string address, int nconns)
		{
			data [address] = (uint) nconns;
		}

		public void Remove (string address)
		{
			// Removing non-existent address is fine.
			data.Remove (address);
		}

		public void Clear ()
		{
			data.Clear ();
		}

		public uint GetMaxConnections (string hostOrIP)
		{
		    uint nconns;

		    if (!data.TryGetValue(hostOrIP, out nconns))
		    {
		        if (!data.TryGetValue("*", out nconns))
		            return defaultMaxConnections;
		    }

            Debug.Assert(nconns > 0, "The number of connections must be positive");

            return nconns;
		}

		public IDictionary<string, uint> Data {
			get { return data; }
		}
	}

	class ConnectionManagementHandler //: IConfigurationSectionHandler
	{
		public virtual object Create (object parent, object configContext, XmlNode section)
		{
			ConnectionManagementData cmd = new ConnectionManagementData (parent);
#if (XML_DEP)			
			if (section.Attributes != null && section.Attributes.Count != 0)
				HandlersUtil.ThrowException ("Unrecognized attribute", section);

			XmlNodeList httpHandlers = section.ChildNodes;
			foreach (XmlNode child in httpHandlers) {
				XmlNodeType ntype = child.NodeType;
				if (ntype == XmlNodeType.Whitespace || ntype == XmlNodeType.Comment)
					continue;

				if (ntype != XmlNodeType.Element)
					HandlersUtil.ThrowException ("Only elements allowed", child);
				
				string name = child.Name;
				if (name == "clear") {
					if (child.Attributes != null && child.Attributes.Count != 0)
						HandlersUtil.ThrowException ("Unrecognized attribute", child);

					cmd.Clear ();
					continue;
				}

				//LAMESPEC: the MS doc says that <remove name="..."/> but they throw an exception
				// if you use that. "address" is correct.

				string address = HandlersUtil.ExtractAttributeValue ("address", child);
				if (name == "add") {
					string maxcnc = HandlersUtil.ExtractAttributeValue ("maxconnection", child, true);
					if (child.Attributes != null && child.Attributes.Count != 0)
						HandlersUtil.ThrowException ("Unrecognized attribute", child);

					cmd.Add (address, maxcnc);
					continue;
				}

				if (name == "remove") {
					if (child.Attributes != null && child.Attributes.Count != 0)
						HandlersUtil.ThrowException ("Unrecognized attribute", child);

					cmd.Remove (address);
					continue;
				}

				HandlersUtil.ThrowException ("Unexpected element", child);
			}
#endif			

			return cmd;
		}
	}

	internal class HandlersUtil
	{
		private HandlersUtil ()
		{
		}
#if (XML_DEP)
		static internal string ExtractAttributeValue (string attKey, XmlNode node)
		{
			return ExtractAttributeValue (attKey, node, false);
		}
			
		static internal string ExtractAttributeValue (string attKey, XmlNode node, bool optional)
		{
			if (node.Attributes == null) {
				if (optional)
					return null;

				ThrowException ("Required attribute not found: " + attKey, node);
			}

			XmlNode att = node.Attributes.RemoveNamedItem (attKey);
			if (att == null) {
				if (optional)
					return null;
				ThrowException ("Required attribute not found: " + attKey, node);
			}

			string value = att.Value;
			if (value == String.Empty) {
				string opt = optional ? "Optional" : "Required";
				ThrowException (opt + " attribute is empty: " + attKey, node);
			}

			return value;
		}

		static internal void ThrowException (string msg, XmlNode node)
		{
			if (node != null && node.Name != String.Empty)
				msg = msg + " (node name: " + node.Name + ") ";
			throw new ConfigurationException (msg, node);
		}
#endif
	}
}

