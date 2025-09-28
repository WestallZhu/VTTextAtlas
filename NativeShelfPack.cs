// VT Shelf packer
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Renderloom.BinUtil;

namespace Renderloom
{
    public struct Shelf
    {
        public int y, h;
        public int firstSeg; // head index in _segs
        public byte alive;
    }

    public struct Seg
    {
        public int shelf;
        public int x, w;
        public int nextInShelf;
        public int nextInBin;
        public byte alive;
    }

    public struct NativeIntRect
    {
        public int x, y, w, h;
        public int xMax => x + w;
        public int yMax => y + h;

        public NativeIntRect(int x, int y, int w, int h)
        { this.x = x; this.y = y; this.w = w; this.h = h; }

        public bool Contains(in NativeIntRect b)
            => b.x >= x && b.y >= y && b.xMax <= xMax && b.yMax <= yMax;
    }

    public static class BinUtil
    {
        [BurstCompile]
        public static int Log2Ceil(int v)
        {
            v = math.max(1, v);
            int l = 32 - math.lzcnt(v - 1);
            return math.max(0, l - 1);
        }
    }

    public unsafe struct NativeShelfPack : System.IDisposable
    {
        int _W, _H;
        int _nextY;

        NativeList<Shelf> _shelves;
        NativeList<Seg>   _segs;
        NativeList<int>   _segFree;

        NativeHashMap<int,int> _binHead; // (hBin<<8)|wBin -> segIdx

        public NativeShelfPack(int atlasW, int atlasH, Allocator alloc)
        {
            _W = atlasW; _H = atlasH; _nextY = 0;
            _shelves = new NativeList<Shelf>(8, alloc);
            _segs    = new NativeList<Seg>(32, alloc);
            _segFree = new NativeList<int>(16, alloc);
            _binHead = new NativeHashMap<int,int>(64, alloc);
        }

        public void Dispose()
        {
            if (_shelves.IsCreated) _shelves.Dispose();
            if (_segs.IsCreated)    _segs.Dispose();
            if (_segFree.IsCreated) _segFree.Dispose();
            if (_binHead.IsCreated) _binHead.Dispose();
        }

        static int MakeShelfKey(int shelfHeight, int segWidth)
        {
            int hb = Log2Ceil(math.max(1, shelfHeight));
            int wb = Log2Ceil(math.max(1, segWidth));
            return (hb << 8) | wb;
        }

        int AllocSeg()
        {
            if (_segFree.Length > 0)
            {
                int id = _segFree[_segFree.Length - 1];
                _segFree.RemoveAtSwapBack(_segFree.Length - 1);
                return id;
            }
            _segs.Add(default);
            return _segs.Length - 1;
        }

        void FreeSeg(int idx)
        {
            _segs[idx] = default;
            _segFree.Add(idx);
        }

        void BinInsert(int key, int segIdx)
        {
            if (_binHead.TryGetValue(key, out int head))
            {
                var s = _segs[segIdx];
                s.nextInBin = head;
                _segs[segIdx] = s;
                _binHead[key] = segIdx;
            }
            else
            {
                var s = _segs[segIdx];
                s.nextInBin = -1;
                _segs[segIdx] = s;
                _binHead.TryAdd(key, segIdx);
            }
        }

        void BinRemove(int key, int segIdx)
        {
            if (!_binHead.TryGetValue(key, out int head)) return;
            int prev = -1, cur = head;
            while (cur != -1)
            {
                if (cur == segIdx)
                {
                    int nxt = _segs[cur].nextInBin;
                    if (prev < 0) _binHead[key] = nxt;
                    else
                    {
                        var p = _segs[prev];
                        p.nextInBin = nxt; _segs[prev] = p;
                    }
                    var cc = _segs[cur]; cc.nextInBin = -1; _segs[cur] = cc;
                    return;
                }
                prev = cur; cur = _segs[cur].nextInBin;
            }
        }

        void ShelfInsertSeg(int shelfIdx, int segIdx)
        {
            var s = _shelves[shelfIdx];
            var g = _segs[segIdx];
            g.nextInShelf = s.firstSeg;
            _segs[segIdx] = g;
            s.firstSeg = segIdx;
            _shelves[shelfIdx] = s;
        }

        void ShelfRemoveSeg(int shelfIdx, int segIdx)
        {
            var s = _shelves[shelfIdx];
            int prev = -1, cur = s.firstSeg;
            while (cur != -1)
            {
                if (cur == segIdx)
                {
                    int nxt = _segs[cur].nextInShelf;
                    if (prev < 0) s.firstSeg = nxt;
                    else { var pp = _segs[prev]; pp.nextInShelf = nxt; _segs[prev] = pp; }
                    _shelves[shelfIdx] = s;
                    var gg = _segs[cur]; gg.nextInShelf = -1; _segs[cur] = gg;
                    return;
                }
                prev = cur; cur = _segs[cur].nextInShelf;
            }
        }

        int CreateShelf(int h)
        {
            if (_nextY + h > _H) return -1;
            var shelf = new Shelf { y = _nextY, h = h, firstSeg = -1, alive = 1 };
            _shelves.Add(shelf);
            int sIdx = _shelves.Length - 1;

            int seg = AllocSeg();
            _segs[seg] = new Seg {
                shelf = sIdx, x = 0, w = _W,
                nextInShelf = -1, nextInBin = -1, alive = 1
            };
            ShelfInsertSeg(sIdx, seg);
            int key = MakeShelfKey(h, _W);
            BinInsert(key, seg);

            _nextY += h;
            return sIdx;
        }

        public void ReclaimTopEmptyShelves()
        {
            for (int i = _shelves.Length - 1; i >= 0; i--)
            {
                var s = _shelves[i];
                if (s.alive == 0) continue;
                if (s.firstSeg != -1)
                {
                    var seg = _segs[s.firstSeg];
                    bool onlyOne = (seg.nextInShelf == -1);
                    if (onlyOne && seg.x == 0 && seg.w == _W)
                    {
                        // remove from bin
                        int key = MakeShelfKey(s.h, seg.w);
                        BinRemove(key, s.firstSeg);
                        FreeSeg(s.firstSeg);

                        _nextY = s.y;
                        s.alive = 0; s.firstSeg = -1;
                        _shelves[i] = s;
                        continue;
                    }
                }
                break;
            }
        }

        public static int QuantizeHeight(int h, int step)
        {
            if (step <= 1) return math.max(1, h);
            int v = math.max(step, h);
            return ((v + step - 1) / step) * step;
        }

        // Try alloc W/H with padding included
        public bool TryAlloc(int wantW, int wantH, int heightStep, out int shelfIdx, out NativeIntRect placedWithPad)
        {
            shelfIdx = -1; placedWithPad = default;
            if (wantW > _W || wantH > _H) return false;

            int hq = QuantizeHeight(wantH, heightStep);
            int needW = wantW;
            int startHB = Log2Ceil(hq);
            int bestSeg = -1, bestKey = 0, bestWaste = int.MaxValue;

            for (int hb = startHB; hb <= Log2Ceil(_H); hb++)
            {
                for (int wb = Log2Ceil(needW); wb <= Log2Ceil(_W); wb++)
                {
                    int key = (hb << 8) | wb;
                    if (!_binHead.TryGetValue(key, out int cur)) continue;
                    while (cur != -1)
                    {
                        var seg = _segs[cur];
                        if (seg.alive == 1)
                        {
                            var s = _shelves[seg.shelf];
                            if (s.alive == 1 && s.h >= hq && seg.w >= needW)
                            {
                                int waste = seg.w - needW;
                                if (waste < bestWaste)
                                {
                                    bestWaste = waste; bestSeg = cur; bestKey = key;
                                    if (waste == 0) break;
                                }
                            }
                        }
                        cur = _segs[cur].nextInBin;
                    }
                    if (bestWaste == 0) break;
                }
                if (bestWaste == 0) break;
            }

            if (bestSeg >= 0)
            {
                var seg = _segs[bestSeg];
                var s   = _shelves[seg.shelf];

                BinRemove(bestKey, bestSeg);

                int px = seg.x;
                int py = s.y;

                seg.x += needW;
                seg.w -= needW;

                if (seg.w > 0)
                {
                    _segs[bestSeg] = seg;
                    int nkey = MakeShelfKey(s.h, seg.w);
                    BinInsert(nkey, bestSeg);
                }
                else
                {
                    ShelfRemoveSeg(seg.shelf, bestSeg);
                    FreeSeg(bestSeg);
                }

                shelfIdx = seg.shelf;
                placedWithPad = new NativeIntRect(px, py, needW, s.h);
                return true;
            }

            int ns = CreateShelf(hq);
            if (ns < 0) return false;

            int first = _shelves[ns].firstSeg;
            var fseg  = _segs[first];
            int fkey  = MakeShelfKey(_shelves[ns].h, fseg.w);
            BinRemove(fkey, first);

            int px2 = fseg.x; int py2 = _shelves[ns].y;
            fseg.x += needW; fseg.w -= needW;

            if (fseg.w > 0)
            {
                _segs[first] = fseg;
                int nkey = MakeShelfKey(_shelves[ns].h, fseg.w);
                BinInsert(nkey, first);
            }
            else
            {
                ShelfRemoveSeg(ns, first);
                FreeSeg(first);
            }

            shelfIdx = ns;
            placedWithPad = new NativeIntRect(px2, py2, needW, _shelves[ns].h);
            return true;
        }

        // Free a horizontal padded segment back to its shelf and merge neighbors
        // Insert by x-order, then merge adjacent in one pass
public void Free(int shelfIdx, int xPad, int wPad)
{
    if (shelfIdx < 0 || shelfIdx >= _shelves.Length || wPad <= 0) return;

    var shelf = _shelves[shelfIdx];
    if (shelf.alive == 0) return;

    // 1) Find insert position by x in shelf: prev -> cur
    int prev = -1, cur = shelf.firstSeg;
    while (cur != -1 && _segs[cur].x < xPad)
    {
        prev = cur;
        cur = _segs[cur].nextInShelf;
    }

    // 2) Create new seg; do not insert into bin yet to avoid transient inconsistency
    int sIdx = AllocSeg();
    _segs[sIdx] = new Seg
    {
        shelf = shelfIdx, x = xPad, w = wPad,
        nextInShelf = -1, nextInBin = -1, alive = 1
    };

    // 3) Insert into shelf list: after prev or as head
    if (prev < 0)
    {
        _segs.GetUnsafePtr()[sIdx].nextInShelf = shelf.firstSeg;
        shelf.firstSeg = sIdx;
        _shelves[shelfIdx] = shelf;
    }
    else
    {
        var p = _segs[prev];
        _segs.GetUnsafePtr()[sIdx].nextInShelf = p.nextInShelf;
        p.nextInShelf = sIdx;
        _segs[prev] = p;
    }

    // 4) Merge with left neighbor if contiguous
    if (prev != -1)
    {
        var left = _segs[prev];
        var me   = _segs[sIdx];
        if (left.x + left.w == me.x)
        {
            // Remove left from bin to avoid stale entry
            BinRemove(MakeShelfKey(shelf.h, left.w), prev);

            // Detach left from shelf list
            ShelfRemoveSeg(shelfIdx, prev);

            // Merge into me
            me.x = left.x;
            me.w += left.w;
            _segs[sIdx] = me;

            // Free left segment
            FreeSeg(prev);

            // Note: sIdx may shift; prev not needed for right merge
        }
    }

    // 5) Merge with right neighbor if contiguous
    int next = _segs[sIdx].nextInShelf;
    if (next != -1)
    {
        var me    = _segs[sIdx];
        var right = _segs[next];
        if (me.x + me.w == right.x)
        {
            // Remove right from bin
            BinRemove(MakeShelfKey(shelf.h, right.w), next);

            // Fix links: me.next = right.next
            var me2 = _segs[sIdx];
            me2.nextInShelf = right.nextInShelf;
            _segs[sIdx] = me2;

            // Merge
            me2.w += right.w;
            _segs[sIdx] = me2;

            // Free right segment
            FreeSeg(next);
        }
    }

    // 6) Insert merged seg back into bin
    BinInsert(MakeShelfKey(_shelves[shelfIdx].h, _segs[sIdx].w), sIdx);
}


#if UNITY_EDITOR
        public void DebugCopyFreeRects(ref NativeList<NativeIntRect> outRects)
        {
            outRects.Clear();
            for (int i = 0; i < _shelves.Length; i++)
            {
                var s = _shelves[i];
                if (s.alive == 0) continue;
                int cur = s.firstSeg;
                while (cur != -1)
                {
                    var g = _segs[cur];
                    if (g.alive == 1)
                        outRects.Add(new NativeIntRect(g.x, s.y, g.w, s.h));
                    cur = g.nextInShelf;
                }
            }
        }
#endif
    }
}

