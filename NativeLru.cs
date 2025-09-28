using Unity.Collections;
using System.Runtime.CompilerServices;

namespace Renderloom
{
    public struct NativeLru : System.IDisposable
    {
        public struct LruNode { public int prev; public int next; }

        private NativeArray<LruNode> _nodes;   // per-id {prev,next}
        private int _head;                     // oldest id or -1
        private int _tail;                     // newest id or -1
        private int _count;
        private int _capacity;
        private Allocator _alloc;

        private const int NULL  = -1;
        private const int NOTIN = -2;          // not in list

        public int Count    => _count;
        public int HeadId   => _head;
        public int TailId   => _tail;
        public int Capacity => _capacity;

        public NativeLru(int capacity, Allocator alloc)
        {
            _capacity = capacity;
            _alloc = alloc;
            _nodes = new NativeArray<LruNode>(capacity, alloc, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < capacity; i++) _nodes[i] = new LruNode { prev = NOTIN, next = NOTIN };
            _head = NULL; _tail = NULL; _count = 0;
        }

        public void Dispose()
        {
            if (_nodes.IsCreated) _nodes.Dispose();
            _head = _tail = NULL; _count = 0; _capacity = 0;
        }

        public void Clear()
        {
            for (int i = 0; i < _capacity; i++) _nodes[i] = new LruNode { prev = NOTIN, next = NOTIN };
            _head = _tail = NULL; _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int id)
        {
            return (uint)id < (uint)_capacity && _nodes[id].prev != NOTIN;
        }

        /// <summary>
        /// If id not present: append to tail. If present and not tail: move to tail. If already tail: no-op.
        /// Typical: call when refCount transitions 1->0 (becoming evictable), or when refreshing zero-ref recency.
        /// </summary>
        public bool AddOrTouch(int id)
        {
            if ((uint)id >= (uint)_capacity) return false;

            if (!Contains(id))
            {
                var n = _nodes[id];
                n.prev = _tail; n.next = NULL; _nodes[id] = n;
                if (_tail != NULL) { var t = _nodes[_tail]; t.next = id; _nodes[_tail] = t; }
                _tail = id;
                if (_head == NULL) _head = id;
                _count++;
                return true;
            }

            if (id == _tail) return true;

            var node = _nodes[id];
            int p = node.prev;
            int q = node.next;
            if (p != NULL) { var tp = _nodes[p]; tp.next = q; _nodes[p] = tp; } else { _head = q; }
            if (q != NULL) { var tq = _nodes[q]; tq.prev = p; _nodes[q] = tq; }

            node.prev = _tail; node.next = NULL; _nodes[id] = node;
            if (_tail != NULL) { var t = _nodes[_tail]; t.next = id; _nodes[_tail] = t; }
            _tail = id;
            if (_head == NULL) _head = id;
            return true;
        }

        /// <summary>Remove id if present (O(1)). Call when refCount transitions 0->1.</summary>
        public bool Remove(int id)
        {
            if (!Contains(id)) return false;

            var node = _nodes[id];
            int p = node.prev;
            int q = node.next;

            if (p != NULL) { var tp = _nodes[p]; tp.next = q; _nodes[p] = tp; } else { _head = q; }
            if (q != NULL) { var tq = _nodes[q]; tq.prev = p; _nodes[q] = tq; } else { _tail = p; }

            _nodes[id] = new LruNode { prev = NOTIN, next = NOTIN };
            _count--;
            if (_count == 0) { _head = _tail = NULL; }
            return true;
        }

        /// <summary>Pop oldest id (head). Returns false if empty.</summary>
        public bool PopHead(out int id)
        {
            id = _head;
            if (id == NULL) return false;

            var node = _nodes[id];
            int q = node.next;
            if (q != NULL) { var tq = _nodes[q]; tq.prev = NULL; _nodes[q] = tq; }
            _head = q;
            if (_tail == id) _tail = q;

            _nodes[id] = new LruNode { prev = NOTIN, next = NOTIN };
            _count--;
            if (_count == 0) { _head = _tail = NULL; }
            return true;
        }
    }
}
