#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2015 Google Inc.  All rights reserved.
// https://developers.google.com/protocol-buffers/
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//     * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//     * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Google.Protobuf.Collections
{
	using System.Runtime;
	using System.Runtime.Versioning;
	using System.Diagnostics;
	using System.Collections.ObjectModel;

	/// <summary>
	/// The contents of a repeated field: essentially, a collection with some extra
	/// restrictions (no null values) and capabilities (deep cloning).
	/// </summary>
	/// <remarks>
	/// This implementation does not generally prohibit the use of types which are not
	/// supported by Protocol Buffers but nor does it guarantee that all operations will work in such cases.
	/// </remarks>
	/// <typeparam name="T">The element type of the repeated field.</typeparam>
	public sealed partial class RepeatedField<T> : IList<T>, IList, IDeepCloneable<RepeatedField<T>>, IEquatable<RepeatedField<T>>
#if !NET35
        , IReadOnlyList<T>
#endif
    {
		internal const int MaxArrayLength = 0X7FEFFFFF;
		internal const int MaxByteArrayLength = 0x7FFFFFC7;

		private static readonly EqualityComparer<T> EqualityComparer = ProtobufEqualityComparers.GetEqualityComparer<T>();
        private static readonly T[] _emptyArray = new T[0];

		private const int _defaultCapacity = 4;

		private T[] _items;
		private int _size;
		private int _version;

		// Constructs a List. The list is initially empty and has a capacity
		// of zero. Upon adding the first element to the list the capacity is
		// increased to 16, and then increased in multiples of two as required.
		public RepeatedField()
		{
			_items = _emptyArray;
		}

		// Constructs a List with a given initial capacity. The list is
		// initially empty, but will have room for the given number of elements
		// before any reallocations are required.
		// 
		public RepeatedField(int capacity)
		{
			if (capacity < 0)
				throw new ArgumentOutOfRangeException("capacity is lower than 0");

			if (capacity == 0)
				_items = _emptyArray;
			else
				_items = new T[capacity];
		}

		// Constructs a List, copying the contents of the given collection. The
		// size and capacity of the new list will both be equal to the size of the
		// given collection.
		// 
		public RepeatedField(IEnumerable<T> collection)
		{
			ProtoPreconditions.CheckNotNull(collection, nameof(collection));

			ICollection<T> c = collection as ICollection<T>;
			if (c != null)
			{
				int count = c.Count;
				if (count == 0)
				{
					_items = _emptyArray;
				}
				else
				{
					_items = new T[count];
					c.CopyTo(_items, 0);
					_size = count;
				}
			}
			else
			{
				_size = 0;
				_items = _emptyArray;
				// This enumerable could be empty.  Let Add allocate a new array, if needed.
				// Note it will also go to _defaultCapacity first, not 1, then 2, etc.

				using (IEnumerator<T> en = collection.GetEnumerator())
				{
					while (en.MoveNext())
					{
						Add(en.Current);
					}
				}
			}
		}

		// Gets and sets the capacity of this list.  The capacity is the size of
		// the internal array used to hold items.  When set, the internal 
		// array of the list is reallocated to the given capacity.
		// 
		public int Capacity
		{
			get
			{
				//Contract.Ensures(Contract.Result<int>() >= 0);
				return _items.Length;
			}
			set
			{
				if (value < _size)
				{
					throw new ArgumentOutOfRangeException("value "+ value+" is smaller than size "+_size);
				}

				if (value != _items.Length)
				{
					if (value > 0)
					{
						T[] newItems = new T[value];
						if (_size > 0)
						{
							Array.Copy(_items, 0, newItems, 0, _size);
						}
						_items = newItems;
					}
					else
					{
						_items = _emptyArray;
					}
				}
			}
		}

		// Read-only property describing how many elements are in the List.
		public int Count
		{
			get
			{
				//Contract.Ensures(Contract.Result<int>() >= 0);
				return _size;
			}
		}

		/// <summary>
		/// Creates a deep clone of this repeated field.
		/// </summary>
		/// <remarks>
		/// If the field type is
		/// a message type, each element is also cloned; otherwise, it is
		/// assumed that the field type is primitive (including string and
		/// bytes, both of which are immutable) and so a simple copy is
		/// equivalent to a deep clone.
		/// </remarks>
		/// <returns>A deep clone of this repeated field.</returns>
		public RepeatedField<T> Clone()
        {
            RepeatedField<T> clone = new RepeatedField<T>();
            if (_items != _emptyArray)
            {
                clone._items = (T[])_items.Clone();
                IDeepCloneable<T>[] cloneableArray = clone._items as IDeepCloneable<T>[];
                if (cloneableArray != null)
                {
                    for (int i = 0; i < _size; i++)
                    {
                        clone._items[i] = cloneableArray[i].Clone();
                    }
                }
            }
            clone._size = _size;
            return clone;
        }

        /// <summary>
        /// Adds the entries from the given input stream, decoding them with the specified codec.
        /// </summary>
        /// <param name="input">The input stream to read from.</param>
        /// <param name="codec">The codec to use in order to read each entry.</param>
        public void AddEntriesFrom(CodedInputStream input, FieldCodec<T> codec)
        {
            // TODO: Inline some of the Add code, so we can avoid checking the size on every
            // iteration.
            uint tag = input.LastTag;
            var reader = codec.ValueReader;
            // Non-nullable value types can be packed or not.
            if (FieldCodec<T>.IsPackedRepeatedField(tag))
            {
                int length = input.ReadLength();
                if (length > 0)
                {
                    int oldLimit = input.PushLimit(length);
                    while (!input.ReachedLimit)
                    {
                        Add(reader(input));
                    }
                    input.PopLimit(oldLimit);
                }
                // Empty packed field. Odd, but valid - just ignore.
            }
            else
            {
                // Not packed... (possibly not packable)
                do
                {
                    Add(reader(input));
                } while (input.MaybeConsumeTag(tag));
            }
        }

        /// <summary>
        /// Calculates the size of this collection based on the given codec.
        /// </summary>
        /// <param name="codec">The codec to use when encoding each field.</param>
        /// <returns>The number of bytes that would be written to a <see cref="CodedOutputStream"/> by <see cref="WriteTo"/>,
        /// using the same codec.</returns>
        public int CalculateSize(FieldCodec<T> codec)
        {
            if (_size == 0)
            {
                return 0;
            }
            uint tag = codec.Tag;
            if (codec.PackedRepeatedField)
            {
                int dataSize = CalculatePackedDataSize(codec);
                return CodedOutputStream.ComputeRawVarint32Size(tag) +
                    CodedOutputStream.ComputeLengthSize(dataSize) +
                    dataSize;
            }
            else
            {
                var sizeCalculator = codec.ValueSizeCalculator;
                int size = _size * CodedOutputStream.ComputeRawVarint32Size(tag);
                for (int i = 0; i < _size; i++)
                {
                    size += sizeCalculator(_items[i]);
                }
                return size;
            }
        }

        private int CalculatePackedDataSize(FieldCodec<T> codec)
        {
            int fixedSize = codec.FixedSize;
            if (fixedSize == 0)
            {
                var calculator = codec.ValueSizeCalculator;
                int tmp = 0;
                for (int i = 0; i < _size; i++)
                {
                    tmp += calculator(_items[i]);
                }
                return tmp;
            }
            else
            {
                return fixedSize * Count;
            }
        }

        /// <summary>
        /// Writes the contents of this collection to the given <see cref="CodedOutputStream"/>,
        /// encoding each value using the specified codec.
        /// </summary>
        /// <param name="output">The output stream to write to.</param>
        /// <param name="codec">The codec to use when encoding each value.</param>
        public void WriteTo(CodedOutputStream output, FieldCodec<T> codec)
        {
            if (_size == 0)
            {
                return;
            }
            var writer = codec.ValueWriter;
            var tag = codec.Tag;
            if (codec.PackedRepeatedField)
            {
                // Packed primitive type
                uint size = (uint)CalculatePackedDataSize(codec);
                output.WriteTag(tag);
                output.WriteRawVarint32(size);
                for (int i = 0; i < _size; i++)
                {
                    writer(output, _items[i]);
                }
            }
            else
            {
                // Not packed: a simple tag/value pair for each value.
                // Can't use codec.WriteTagAndValue, as that omits default values.
                for (int i = 0; i < _size; i++)
                {
                    output.WriteTag(tag);
                    writer(output, _items[i]);
                }
            }
        }

		// Ensures that the capacity of this list is at least the given minimum
		// value. If the currect capacity of the list is less than min, the
		// capacity is increased to twice the current capacity or to min,
		// whichever is larger.
		private void EnsureCapacity(int min)
		{
			if (_items.Length < min)
			{
				int newCapacity = _items.Length == 0 ? _defaultCapacity : _items.Length * 2;
				// Allow the list to grow to maximum possible capacity (~2G elements) before encountering overflow.
				// Note that this check works even when _items.Length overflowed thanks to the (uint) cast
				if ((uint)newCapacity > MaxArrayLength) newCapacity = MaxArrayLength;
				if (newCapacity < min) newCapacity = min;
				Capacity = newCapacity;
			}
		}

		// Adds the given object to the end of this list. The size of the list is
		// increased by one. If required, the capacity of the list is doubled
		// before adding the new element.
		//
		public void Add(T item)
		{
			ProtoPreconditions.CheckNotNullUnconstrained(item, nameof(item));

			if (_size == _items.Length) EnsureCapacity(_size + 1);
			_items[_size++] = item;
			_version++;
		}

		// Clears the contents of List.
		public void Clear()
		{
			if (_size > 0)
			{
				Array.Clear(_items, 0, _size); // Don't need to doc this but we clear the elements so that the gc can reclaim the references.
				_size = 0;
			}
			_version++;
		}

		/// <summary>
		/// Determines whether this collection contains the given item.
		/// </summary>
		/// <param name="item">The item to find.</param>
		/// <returns><c>true</c> if this collection contains the given item; <c>false</c> otherwise.</returns>
		public bool Contains(T item)
        {
            return IndexOf(item) != -1;
        }

		// Copies a section of this list to the given array at the given index.
		// 
		// The method uses the Array.Copy method to copy the elements.
		// 
		public void CopyTo(int index, T[] array, int arrayIndex, int count)
		{
			if (_size - index < count)
			{
				throw new ArgumentException("Argument_InvalidOffLen");
			}

			// Delegate rest of error checking to Array.Copy.
			Array.Copy(_items, index, array, arrayIndex, count);
		}

		public void CopyTo(T[] array, int arrayIndex)
		{
			// Delegate rest of error checking to Array.Copy.
			Array.Copy(_items, 0, array, arrayIndex, _size);
		}

		// Removes the element at the given index. The size of the list is
		// decreased by one.
		// 
		public bool Remove(T item)
		{
			int index = IndexOf(item);
			if (index >= 0)
			{
				RemoveAt(index);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Gets a value indicating whether the collection is read-only.
		/// </summary>
		public bool IsReadOnly => false;

		// Adds the elements of the given collection to the end of this list. If
		// required, the capacity of the list is increased to twice the previous
		// capacity or the new size, whichever is larger.
		//
		// Original Google Protobuf would check if each item in the collection is null or not. Considering its performance overhead, it's dropped
		/**
		 * foreach (T item in collection){
		 *	ProtoPreconditions.CheckNotNullUnconstrained(item, nameof(item));
		 * }
		 */
		public void AddRange(IEnumerable<T> collection)
		{
			//Contract.Ensures(Count >= Contract.OldValue(Count));

			InsertRange(_size, collection);
		}

		// Inserts the elements of the given collection at a given index. If
		// required, the capacity of the list is increased to twice the previous
		// capacity or the new size, whichever is larger.  Ranges may be added
		// to the end of the list by setting index to the List's size.
		//
		public void InsertRange(int index, IEnumerable<T> collection)
		{
			ProtoPreconditions.CheckNotNull(collection, nameof(collection));

			if ((uint)index > (uint)_size)
			{
				throw new ArgumentOutOfRangeException("index "+ index+" is greater than size "+_size);
			}

			ICollection<T> c = collection as ICollection<T>;
			if (c != null)
			{    // if collection is ICollection<T>
				int count = c.Count;
				if (count > 0)
				{
					EnsureCapacity(_size + count);
					if (index < _size)
					{
						Array.Copy(_items, index, _items, index + count, _size - index);
					}

					// If we're inserting a List into itself, we want to be able to deal with that.
					if (this == c)
					{
						// Copy first part of _items to insert location
						Array.Copy(_items, 0, _items, index, index);
						// Copy last part of _items back to inserted location
						Array.Copy(_items, index + count, _items, index * 2, _size - index);
					}
					else
					{
						T[] itemsToInsert = new T[count];
						c.CopyTo(itemsToInsert, 0);
						itemsToInsert.CopyTo(_items, index);
					}
					_size += count;
				}
			}
			else
			{
				using (IEnumerator<T> en = collection.GetEnumerator())
				{
					while (en.MoveNext())
					{
						Insert(index++, en.Current);
					}
				}
			}
			_version++;
		}

		/// <summary>
		/// Adds all of the specified values into this collection. This method is present to
		/// allow repeated fields to be constructed from queries within collection initializers.
		/// Within non-collection-initializer code, consider using the equivalent <see cref="AddRange"/>
		/// method instead for clarity.
		/// </summary>
		/// <param name="values">The values to add to this collection.</param>
		public void Add(IEnumerable<T> values)
        {
            AddRange(values);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// An enumerator that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _size; i++)
            {
                yield return _items[i];
            }
        }

        /// <summary>
        /// Determines whether the specified <see cref="System.Object" />, is equal to this instance.
        /// </summary>
        /// <param name="obj">The <see cref="System.Object" /> to compare with this instance.</param>
        /// <returns>
        ///   <c>true</c> if the specified <see cref="System.Object" /> is equal to this instance; otherwise, <c>false</c>.
        /// </returns>
        public override bool Equals(object obj)
        {
            return Equals(obj as RepeatedField<T>);
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Collections.IEnumerator" /> object that can be used to iterate through the collection.
        /// </returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Returns a hash code for this instance.
        /// </summary>
        /// <returns>
        /// A hash code for this instance, suitable for use in hashing algorithms and data structures like a hash table. 
        /// </returns>
        public override int GetHashCode()
        {
            int hash = 0;
            for (int i = 0; i < _size; i++)
            {
                hash = hash * 31 + _items[i].GetHashCode();
            }
            return hash;
        }

        /// <summary>
        /// Compares this repeated field with another for equality.
        /// </summary>
        /// <param name="other">The repeated field to compare this with.</param>
        /// <returns><c>true</c> if <paramref name="other"/> refers to an equal repeated field; <c>false</c> otherwise.</returns>
        public bool Equals(RepeatedField<T> other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }
            if (ReferenceEquals(other, this))
            {
                return true;
            }
            if (other.Count != this.Count)
            {
                return false;
            }
            EqualityComparer<T> comparer = EqualityComparer;
            for (int i = 0; i < _size; i++)
            {
                if (!comparer.Equals(_items[i], other._items[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns the index of the given item within the collection, or -1 if the item is not
        /// present.
        /// </summary>
        /// <param name="item">The item to find in the collection.</param>
        /// <returns>The zero-based index of the item, or -1 if it is not found.</returns>
        public int IndexOf(T item)
        {
            ProtoPreconditions.CheckNotNullUnconstrained(item, nameof(item));
            EqualityComparer<T> comparer = EqualityComparer;
            for (int i = 0; i < _size; i++)
            {
                if (comparer.Equals(_items[i], item))
                {
                    return i;
                }
            }
            return -1;
        }

		// Inserts an element into this list at a given index. The size of the list
		// is increased by one. If required, the capacity of the list is doubled
		// before inserting the new element.
		// 
		public void Insert(int index, T item)
		{
			ProtoPreconditions.CheckNotNullUnconstrained(item, nameof(item));

			// Note that insertions at the end are legal.
			if ((uint)index > (uint)_size)
			{
				throw new ArgumentOutOfRangeException("index " + index + " is greater than size " + _size);
			}
			if (_size == _items.Length) EnsureCapacity(_size + 1);
			if (index < _size)
			{
				Array.Copy(_items, index, _items, index + 1, _size - index);
			}
			_items[index] = item;
			_size++;
			_version++;
		}

		// Removes the element at the given index. The size of the list is
		// decreased by one.
		// 
		public void RemoveAt(int index)
		{
			if ((uint)index >= (uint)_size)
			{
				throw new ArgumentOutOfRangeException("index " + index + " is greater than size " + _size);
			}
			_size--;
			if (index < _size)
			{
				Array.Copy(_items, index + 1, _items, index, _size - index);
			}
			_items[_size] = default(T);
			_version++;
		}

		/// <summary>
		/// Returns a string representation of this repeated field, in the same
		/// way as it would be represented by the default JSON formatter.
		/// </summary>
		public override string ToString()
        {
            var writer = new StringWriter();
            JsonFormatter.Default.WriteList(writer, this);
            return writer.ToString();
        }

		/// <summary>
		/// Gets or sets the item at the specified index.
		/// </summary>
		/// <value>
		/// The element at the specified index.
		/// </value>
		/// <param name="index">The zero-based index of the element to get or set.</param>
		/// <returns>The item at the specified index.</returns>
		public T this[int index]
		{
			get
			{
				// Following trick can reduce the range check by one
				if ((uint)index >= (uint)_size)
				{
					throw new ArgumentOutOfRangeException();
				}
				return _items[index];
			}

			set
			{
				if ((uint)index >= (uint)_size)
				{
					throw new ArgumentOutOfRangeException();
				}
				ProtoPreconditions.CheckNotNullUnconstrained(value, nameof(value));
				_items[index] = value;
				_version++;
			}
		}

		#region Explicit interface implementation for IList and ICollection.
		bool IList.IsFixedSize => false;

		// Copies this List into array, which must be of a 
		// compatible array type.  
		//
		void System.Collections.ICollection.CopyTo(Array array, int arrayIndex)
		{
			if ((array != null) && (array.Rank != 1))
			{
				throw new ArgumentException("RankMultiDimNotSupported");
			}

			// Array.Copy will check for NULL.
			Array.Copy(_items, 0, array, arrayIndex, _size);
		}

		bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        object IList.this[int index]
        {
            get { return this[index]; }
            set { this[index] = (T)value; }
        }

		int System.Collections.IList.Add(object item)
		{
			Add((T)item);

			return Count - 1;
		}

		bool IList.Contains(object value)
        {
            return (value is T && Contains((T)value));
        }

        int IList.IndexOf(object value)
        {
            if (!(value is T))
            {
                return -1;
            }
            return IndexOf((T)value);
        }

        void IList.Insert(int index, object value)
        {
            Insert(index, (T) value);
        }

        void IList.Remove(object value)
        {
            if (!(value is T))
            {
                return;
            }
            Remove((T)value);
        }
        #endregion        
    }
}
