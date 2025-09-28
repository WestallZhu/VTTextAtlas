using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Unity.Collections;
using TMPro;

namespace Renderloom
{
    public struct TextBakeParamsNative
    {
        public TMP_FontAsset font;
        public int   pixelHeight;
        public Color32 faceColor;
        public Color32 outlineColor;
        public float outlineWidth;
        public bool  richText;
        public int   padding;

        public void ApplyToTMP(TextMeshPro t)
        {
            t.richText = richText;
            t.font = font;
            t.fontSize = pixelHeight;          // use raw pixel height (no autosizing)
            t.enableAutoSizing = false;
            t.color = faceColor;
            t.outlineColor = outlineColor;
            t.outlineWidth = outlineWidth;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.isOrthographic = true;
            t.enableKerning = true;
        }

        public string Signature()
        {
            return $"{(font?font.GetInstanceID():0)}|{pixelHeight}|{faceColor.r:x2}{faceColor.g:x2}{faceColor.b:x2}{faceColor.a:x2}|{outlineColor.r:x2}{outlineColor.g:x2}{outlineColor.b:x2}{outlineColor.a:x2}|{outlineWidth:F3}|{(richText?1:0)}|P{padding}";
        }
    }

    public struct SpriteUVNative
    {
        public Vector4 atlasRect01;  // xy offset, zw size (0..1)
        public Vector2Int pixelSize; // size in pixels (without padding)
    }

    public struct CacheEntry
    {
        public Hash128 key;
        public int x, y, w, h;        // inner rect (excluding padding)
        public int pxW, pxH;          // = w,h
        public int pad;               // padding
        public int lastUseTick;
        public int refCount;
        public byte alive;

        // Shelf-specific
        public int shelfIdx;
        public int xPad;  // placedWithPad.x
        public int wPad;  // placedWithPad.w (height is shelf.h)
        public int hPad;  // reserved height; = placedWithPad.h; usually shelf.h
    }

    public class VTTextAtlasShelf : MonoBehaviour
    {
        [Header("Atlas")]
        public int atlasSize = 1024;
        public int padding   = 4;
        public int shelfHeightStep = 8; // shelf height step (e.g., 8/16/24...)
        public FilterMode atlasFilter = FilterMode.Bilinear;

        [Header("Shader")]
        public Shader spriteShader; // "Sprites/VTAtlas-Unlit-Unit"

        [Header("Bake (TMP 3D)")]
        public Camera bakeCam;
        public TextMeshPro bakeText;   // 3D TMP

        [Header("Eviction Watermark")]
        [Tooltip("������ʣ��ռ���� <= ��ֵʱ������һ������ǰ���� EvictSome(1)")]
        [Range(0.0f, 1.0f)] public float freeWatermark = 0.20f;

        // Native containers for cache structures
        private NativeHashMap<Hash128,int> _map;  // key -> entry index
        private NativeList<CacheEntry>      _entries;
        private NativeList<int>             _entryAlive;
        private NativeList<int>             _entryFreeStack;

        private NativeLru                    _evictLru;   // O(1) LRU
        private NativeShelfPack              _shelf;

        private RenderTexture _atlas;
        private int _tick;

        // booked area with padding + shelf height
        private long _bookedArea; // sum(placedWithPad.w * placedWithPad.h)

        static VTTextAtlasShelf _inst;
        public static VTTextAtlasShelf Instance => _inst;

        void Awake()
        {
            if (_inst && _inst != this) { Destroy(gameObject); return; }
            _inst = this;

            _atlas = CreateAtlas(atlasSize, atlasFilter);


            _map           = new NativeHashMap<Hash128,int>(1024, Allocator.Persistent);
            _entries       = new NativeList<CacheEntry>(1024, Allocator.Persistent);
            _entryAlive    = new NativeList<int>(1024, Allocator.Persistent);
            _entryFreeStack= new NativeList<int>(128, Allocator.Persistent);

            _evictLru     = new NativeLru(1024, Allocator.Persistent);
            _shelf         = new NativeShelfPack(atlasSize, atlasSize, Allocator.Persistent);

            _bookedArea = 0;
            EnsureBakePipeline();
        }

        void OnDestroy()
        {
            if (_map.IsCreated) _map.Dispose();
            if (_entries.IsCreated) _entries.Dispose();
            if (_entryAlive.IsCreated) _entryAlive.Dispose();
            if (_entryFreeStack.IsCreated) _entryFreeStack.Dispose();
            _evictLru.Dispose();
            _shelf.Dispose();

            if (_atlas) _atlas.Release();

            if (bakeCam) DestroyImmediate(bakeCam.gameObject);
            if (bakeText) DestroyImmediate(bakeText.gameObject);
        }

        RenderTexture CreateAtlas(int size, FilterMode filter)
        {
            var fmt = GraphicsFormat.R8_UNorm;

            var rt = new RenderTexture(new RenderTextureDescriptor(size, size, fmt, 0)
            {
                useMipMap = false, autoGenerateMips = false
            })
            { name = "VT_Atlas_Shelf" };
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = filter;
            rt.Create();

            var cmd = new CommandBuffer { name = "VT_ClearAtlas" };
            cmd.SetRenderTarget(rt);
            cmd.ClearRenderTarget(false, true, new Color(0,0,0,0));
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            return rt;
        }

        void EnsureBakePipeline()
        {
            int layer = LayerMask.NameToLayer("TextVT");
            if (layer < 0) { Debug.LogWarning("Create Layer first: TextVT"); layer = 0; }

            if (!bakeCam)
            {
                var goCam = new GameObject("VT_BakeCam");
                goCam.hideFlags = HideFlags.HideAndDontSave;
                bakeCam = goCam.AddComponent<Camera>();
                bakeCam.orthographic = true;
                bakeCam.clearFlags = CameraClearFlags.SolidColor;
                bakeCam.backgroundColor = new Color(0,0,0,0);
                bakeCam.cullingMask = 1 << layer;
                bakeCam.enabled = false;
                bakeCam.transform.position = new Vector3(0, 0, -10);
                bakeCam.transform.rotation = Quaternion.identity;
            }

            if (!bakeText)
            {
                var goText = new GameObject("VT_TMP3D");
                goText.hideFlags = HideFlags.HideAndDontSave;
                goText.layer = layer;
                bakeText = goText.AddComponent<TextMeshPro>();
                bakeText.raycastTarget = false;
                bakeText.enableWordWrapping = false;
                bakeText.alignment = TextAlignmentOptions.Center;
                bakeText.overflowMode = TextOverflowModes.Overflow;
                bakeText.isOrthographic = true;
                bakeText.transform.position = Vector3.zero;
                bakeText.transform.rotation = Quaternion.identity;
                bakeText.transform.localScale = Vector3.one;
            }
        }

        int AllocEntry()
        {
            if (_entryFreeStack.Length > 0)
            {
                int id = _entryFreeStack[_entryFreeStack.Length - 1];
                _entryFreeStack.RemoveAtSwapBack(_entryFreeStack.Length - 1);
                return id;
            }
            _entries.Add(default);
            _entryAlive.Add(0);
            return _entries.Length - 1;
        }

        void FreeEntry(int idx)
        {
            _entryAlive[idx] = 0;
            _entryFreeStack.Add(idx);
        }

        public RenderTexture GetAtlasRT() => _atlas;

        public static Hash128 MakeKey(string text, in TextBakeParamsNative p, int shelfHeightStep /*unused*/)
        {
            return Hash128.Compute($"{p.Signature()}|{text}");
        }

        // ---- Watermark pre-evict ----
        void MaybePreEvict()
        {
            if (!_atlas) return;
            long total = (long)_atlas.width * (long)_atlas.height;
            float freeRatio = Mathf.Clamp01(1f - (float)_bookedArea / (float)total);
            if (freeRatio <= freeWatermark)
            {
                EvictSome(1);
                _shelf.ReclaimTopEmptyShelves();
            }
        }

        public bool TryGetUV(Hash128 key, out SpriteUVNative uv)
        {
            _tick++;
            if (_map.TryGetValue(key, out int idx) && _entryAlive[idx] == 1)
            {
                var e = _entries[idx];
                if (e.refCount == 0) _evictLru.Remove(idx);

                e.lastUseTick = _tick;
                e.refCount++;
                _entries[idx] = e;

                uv = new SpriteUVNative
                {
                    atlasRect01 = new Vector4((float)e.x/_atlas.width, (float)e.y/_atlas.height,
                                              (float)e.w/_atlas.width, (float)e.h/_atlas.height),
                    pixelSize = new Vector2Int(e.pxW, e.pxH)
                };
                return true;
            }
            uv = default; return false;
        }

        public SpriteUVNative GetOrBake(string text, TextBakeParamsNative p)
        {
            var key = MakeKey(text, p, shelfHeightStep);
            if (TryGetUV(key, out var uvHit)) return uvHit;

            //  Measure with the same font params as TMP
            var (w, h) = MeasureSize(text, p);
            int W = w + padding * 2;
            int H = h + padding * 2;

            // Pre-watermark: if free ratio <= threshold, evict one entry
            MaybePreEvict();

            //  Pack (on failure: evict LRU -> reclaim shelves -> retry)
            NativeIntRect placedWithPad;
            int shelfIdx;
            if (!_shelf.TryAlloc(W, H, shelfHeightStep, out shelfIdx, out placedWithPad))
            {
                EvictSome(8);
                _shelf.ReclaimTopEmptyShelves();
                if (!_shelf.TryAlloc(W, H, shelfHeightStep, out shelfIdx, out placedWithPad))
                {
                    Debug.LogError("VT Shelf: atlas full, cannot allocate");
                    return default;
                }
            }

            //  Bake to a temp RT -> copy to atlas
            var tmp = BakeToTempRT(text, p, W, H);
            CopyIntoAtlas(tmp, placedWithPad);
            RenderTexture.ReleaseTemporary(tmp);

            //  Record entry innerRect (excluding padding)
            int innerX = placedWithPad.x + padding;
            int innerY = placedWithPad.y + padding;
            int innerW = w;
            int innerH = h;

            int id = AllocEntry();
            var e = new CacheEntry
            {
                key = key,
                x = innerX, y = innerY, w = innerW, h = innerH,
                pxW = w, pxH = h,
                pad = padding,
                lastUseTick = ++_tick,
                refCount = 1,
                alive = 1,
                shelfIdx = shelfIdx,
                xPad = placedWithPad.x,
                wPad = placedWithPad.w,
                hPad = placedWithPad.h
            };
            _entries[id] = e; _entryAlive[id] = 1;
            _map.TryAdd(key, id);

            // Update booked area using hPad (reserved height)
            _bookedArea += (long)placedWithPad.w * (long)placedWithPad.h;

            return new SpriteUVNative
            {
                atlasRect01 = new Vector4((float)e.x/_atlas.width, (float)e.y/_atlas.height,
                                          (float)e.w/_atlas.width, (float)e.h/_atlas.height),
                pixelSize = new Vector2Int(w, h)
            };
        }

        public void Release(Hash128 key)
        {
            if (_map.TryGetValue(key, out int idx) && _entryAlive[idx] == 1)
            {
                var e = _entries[idx];
                if (e.refCount > 0)
                {
                    e.refCount -= 1;
                    if (e.refCount == 0)
                        _evictLru.AddOrTouch(idx);
                    _entries[idx] = e;
                }
            }
        }

        (int w, int h) MeasureSize(string text, in TextBakeParamsNative p)
        {
            p.ApplyToTMP(bakeText);
            bakeText.text = text;
            bakeText.ForceMeshUpdate();
            Vector2 pref = bakeText.GetPreferredValues(text);
            int w = Mathf.Max(2, Mathf.CeilToInt(pref.x));
            int h = Mathf.Max(2, Mathf.CeilToInt(pref.y));
            return (w, h);
        }

        RenderTexture BakeToTempRT(string text, in TextBakeParamsNative p, int W, int H)
        {
            var fmt = RenderTextureFormat.R8; 
            var rt = RenderTexture.GetTemporary(W, H, 0, fmt);
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = atlasFilter;

            p.ApplyToTMP(bakeText);
            bakeText.text = text;
            bakeText.alignment = TextAlignmentOptions.Center;
            bakeText.margin = new Vector4(p.padding, p.padding, p.padding, p.padding);
            bakeText.ForceMeshUpdate();

            bakeCam.targetTexture = rt;
            bakeCam.orthographicSize = H * 0.5f; // 1 world unit = 1 pixel
            bakeCam.transform.position = new Vector3(0, 0, -10);
            bakeCam.transform.rotation = Quaternion.identity;

            var tr = bakeText.transform;
            tr.position = Vector3.zero;
            tr.rotation = Quaternion.identity;
            tr.localScale = Vector3.one;

            bakeCam.Render();
            bakeCam.targetTexture = null;
            return rt;
        }

        void CopyIntoAtlas(RenderTexture src, in NativeIntRect dst)
        {
            Graphics.CopyTexture(src, 0, 0, 0, 0, src.width, src.height, _atlas, 0, 0, dst.x, dst.y);
        }

        void EvictSome(int maxCount)
        {
            int evicted = 0;
            while (evicted < maxCount && _evictLru.PopHead(out int idx))
            {
                if (_entryAlive[idx] == 0) continue;
                var e = _entries[idx];
                if (e.refCount != 0) continue;

                // Decrease booked area
                _bookedArea -= (long)e.wPad * (long)e.hPad;
                if (_bookedArea < 0) _bookedArea = 0;

                // Free padded block
                _shelf.Free(e.shelfIdx, e.xPad, e.wPad);

                _map.Remove(e.key);
                _entryAlive[idx] = 0;
                _entries[idx] = default;
                _entryFreeStack.Add(idx);

                evicted++;
            }
        }

#if UNITY_EDITOR
        public struct DebugStats
        {
            public int activeEntries;
            public int freeBlocks;
            public int freeArea;
            public int usedAreaInner;
            public int usedAreaWithPad;
            public float usageInner;
            public float usageWithPad;
            public int evictHeapCount;
            public float freeRatioBooked;
        }

        public void DebugGetRects(
            ref NativeList<NativeIntRect> freeOut,
            ref NativeList<NativeIntRect> usedOut,
            out int texW, out int texH)
        {
            texW = _atlas.width;
            texH = _atlas.height;

            _shelf.DebugCopyFreeRects(ref freeOut);

            usedOut.Clear();
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entryAlive[i] == 1)
                {
                    var e = _entries[i];
                    int px = Mathf.Max(0, e.x - e.pad);
                    int py = Mathf.Max(0, e.y - e.pad);
                    int pw = Mathf.Min(texW - px, e.w + e.pad * 2);
                    int ph = Mathf.Min(texH - py, e.h + e.pad * 2);
                    usedOut.Add(new NativeIntRect(px, py, pw, ph));
                }
            }
        }

        public DebugStats DebugGetStats()
        {
            int texW = _atlas.width, texH = _atlas.height;
            int active = 0, inner = 0;

            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entryAlive[i] == 1)
                {
                    active++;
                    var e = _entries[i];
                    inner += e.w * e.h;
                }
            }

            long total = (long)texW * (long)texH;
            long usedBooked = _bookedArea;
            if (usedBooked < 0) usedBooked = 0;
            if (usedBooked > total) usedBooked = total;
            int usedWithPad = (int)usedBooked;
            int freeArea = (int)(total - usedBooked);
            float freeRatio = total > 0 ? (float)freeArea / (float)total : 0f;

            return new DebugStats
            {
                activeEntries   = active,
                freeBlocks      = -1,            // not tracked for shelf allocator
                freeArea        = freeArea,
                usedAreaInner   = inner,
                usedAreaWithPad = usedWithPad,
                usageInner      = (float)inner / (texW * texH),
                usageWithPad    = (float)usedWithPad / (texW * texH),
                evictHeapCount  = _evictLru.Count,
                freeRatioBooked = freeRatio
            };
        }
#endif
    }
}

