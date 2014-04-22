//
// System.Collections.Specialized.NameValueCollection.cs
//
// Author:
//   Gleb Novodran
//
// (C) Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004-2011 Novell (http://www.novell.com)
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
using System.Linq;
using System.Text;

namespace SM.Mono.Collections.Specialized
{
    public class NameValueCollection
    {
        readonly Dictionary<string, IList<string>> _collection;

        string[] cachedAll;
        string[] cachedAllKeys;

        //--------------------- Constructors -----------------------------

        public NameValueCollection()
        {
            _collection = new Dictionary<string, IList<string>>();
        }

        public NameValueCollection(int capacity)
        {
            _collection = new Dictionary<string, IList<string>>(capacity, StringComparer.InvariantCultureIgnoreCase);
        }

        public NameValueCollection(NameValueCollection col)
        {
            if (col == null)
                throw new ArgumentNullException("col");

            _collection = new Dictionary<string, IList<string>>(col._collection, StringComparer.InvariantCultureIgnoreCase);
        }

        //[Obsolete ("Use NameValueCollection (IEqualityComparer)")]
        //public NameValueCollection (IHashCodeProvider hashProvider, IComparer comparer)
        //    : base (hashProvider, comparer)
        //{

        //}

        public NameValueCollection(int capacity, NameValueCollection col)
        {
            _collection = new Dictionary<string, IList<string>>(col._collection, StringComparer.InvariantCultureIgnoreCase);
        }

        //protected NameValueCollection (SerializationInfo info, StreamingContext context)
        //    :base (info, context)
        //{

        //}

        //[Obsolete ("Use NameValueCollection (IEqualityComparer)")]
        //public NameValueCollection (int capacity, IHashCodeProvider hashProvider, IComparer comparer)
        //    :base (capacity, hashProvider, comparer)
        //{

        //}

        public NameValueCollection(IEqualityComparer<string> equalityComparer)
        {
            _collection = new Dictionary<string, IList<string>>(equalityComparer);
        }

        public NameValueCollection(int capacity, IEqualityComparer<string> equalityComparer)
        {
            _collection = new Dictionary<string, IList<string>>(capacity, equalityComparer);
        }

        public virtual string[] AllKeys
        {
            get
            {
                if (null == cachedAllKeys)
                    cachedAllKeys = _collection.Keys.ToArray();

                return cachedAllKeys;
            }
        }

        protected bool IsReadOnly { get; set; }

        public string this[int index]
        {
            get { return Get(index); }
        }

        public string this[string name]
        {
            get { return Get(name); }
            set { Set(name, value); }
        }

        public int Count
        {
            get { return _collection.Count; }
        }

        public void Add(NameValueCollection c)
        {
            //if (this.IsReadOnly)
            //    throw new NotSupportedException ("Collection is read-only");
            if (c == null)
                throw new ArgumentNullException("c");

            // make sense - but it's not the exception thrown
            //				throw new ArgumentNullException ();

            InvalidateCachedArrays();

            foreach (var kv in c._collection)
            {
                IList<string> values;
                if (!_collection.TryGetValue(kv.Key, out values))
                {
                    values = new List<string>();

                    _collection[kv.Key] = values;
                }

                foreach (var v in kv.Value)
                {
                    if (null != v)
                        values.Add(v);
                }
            }
        }

        /// in SDK doc: If the same value already exists under the same key in the collection, 
        /// it just adds one more value in other words after
        /// <code>
        /// NameValueCollection nvc;
        /// nvc.Add ("LAZY","BASTARD")
        /// nvc.Add ("LAZY","BASTARD")
        /// </code>
        /// nvc.Get ("LAZY") will be "BASTARD,BASTARD" instead of "BASTARD"
        public virtual void Add(string name, string value)
        {
            if (IsReadOnly)
                throw new NotSupportedException("Collection is read-only");

            InvalidateCachedArrays();

            IList<string> values;
            if (!_collection.TryGetValue(name, out values))
            {
                values = new List<string>();

                _collection[name] = values;
            }

            if (null != value)
                values.Add(value);
        }

        public virtual void Clear()
        {
            if (IsReadOnly)
                throw new NotSupportedException("Collection is read-only");

            InvalidateCachedArrays();

            _collection.Clear();
        }

        //public void CopyTo(Array dest, int index)
        //{
        //    if (dest == null)
        //        throw new ArgumentNullException("dest", "Null argument - dest");
        //    if (index < 0)
        //        throw new ArgumentOutOfRangeException("index", "index is less than 0");
        //    if (dest.Rank > 1)
        //        throw new ArgumentException("dest", "multidim");

        //    if (cachedAll == null)
        //        RefreshCachedAll();
        //    try
        //    {
        //        cachedAll.CopyTo(dest, index);
        //    }
        //    catch (ArrayTypeMismatchException)
        //    {
        //        throw new InvalidCastException();
        //    }
        //}

        //public bool IsSynchronized { get; private set; }
        //public object SyncRoot { get; private set; }

        //private void RefreshCachedAll()
        //{
        //    this.cachedAll = null;
        //    int max = this.Count;
        //    cachedAll = new string[max];
        //    for (int i = 0; i < max; i++)
        //        cachedAll[i] = this.Get(i);
        //}

        public virtual string Get(int index)
        {
            var key = AllKeys[index];

            return Get(key);
        }

        public virtual string Get(string name)
        {
            IList<string> values;
            if (!_collection.TryGetValue(name, out values))
                return null;

            return AsSingleString(values);
        }

        static string AsSingleString(IList<string> values)
        {
            const char separator = ',';

            if (values == null)
                return null;

            var max = values.Count;

            switch (max)
            {
                case 0:
                    return null;
                case 1:
                    return values[0];
                case 2:
                    return String.Concat(values[0], separator, values[1]);
                default:
                    var len = max;
                    for (var i = 0; i < max; i++)
                        len += values[i].Length;
                    var sb = new StringBuilder(values[0], len);
                    for (var i = 1; i < max; i++)
                    {
                        sb.Append(separator);
                        sb.Append(values[i]);
                    }

                    return sb.ToString();
            }
        }

        public virtual string GetKey(int index)
        {
            return AllKeys[index];
        }

        public virtual string[] GetValues(int index)
        {
            var key = AllKeys[index];

            return GetValues(key);
        }

        public virtual string[] GetValues(string name)
        {
            var values = _collection[name];

            return values.ToArray();
        }

        public bool HasKeys()
        {
            return _collection.Any();
        }

        public virtual void Remove(string name)
        {
            if (IsReadOnly)
                throw new NotSupportedException("Collection is read-only");

            InvalidateCachedArrays();

            _collection.Remove(name);
        }

        public virtual void Set(string name, string value)
        {
            if (IsReadOnly)
                throw new NotSupportedException("Collection is read-only");

            InvalidateCachedArrays();

            IList<string> values;
            if (_collection.TryGetValue(name, out values))
            {
                values.Clear();
                values.Add(value);
            }
            else
            {
                _collection[name] = new List<string>
                                    {
                                        value
                                    };
            }
        }

        protected void InvalidateCachedArrays()
        {
            cachedAllKeys = null;
            cachedAll = null;
        }

        public IEnumerator GetEnumerator()
        {
            return AllKeys.GetEnumerator();
        }
    }
}
