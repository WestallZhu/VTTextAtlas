using TMPro;
using UnityEngine;

namespace Renderloom
{
    [RequireComponent(typeof(VTTextAtlasShelf))]
    [RequireComponent(typeof(VTTextBatchRenderer))]
    public class LargeSceneBootstrap_Batched : MonoBehaviour
    {
        [Header("Text Settings")]
        public TMP_FontAsset font;
        public int pixelHeight = 48;
        public int padding = 4;
        public Color faceColor = Color.white;
        public Color outlineColor = new Color(0f, 0f, 0f, 0f);
        public float outlineWidth = 0f;
        public bool richText = false;
        public float pixelsPerUnit = 100f;

        [Header("Danmaku Spawning")]
        [Tooltip("Items spawned per second.")]
        public float spawnRate = 60f;
        [Tooltip("Lifetime range (seconds) for each spawned text.")]
        public Vector2 lifetimeRange = new Vector2(0.6f, 1.6f);
        [Tooltip("Maximum concurrently active texts (avoid runaway growth). 0 = unlimited.")]
        public int maxActive = 500;
        [Tooltip("Seed for deterministic spawning (<=0 to use random).")]
        public int randomSeed = 12345;

        [Header("Text Repetition Weights")] 
        [Range(0,100)] public int commonWeight = 60;
        [Range(0,100)] public int mediumWeight = 30;
        [Range(0,100)] public int rareWeight = 10;

        [Header("Unique Limit")]
        [Tooltip("Upper bound for distinct text variants produced at runtime.")]
        public int maxUniqueTexts = 300;

        [Header("Common Texts (High Repeat)")]
        public string[] commonTexts = new string[]
        {
            "Hello","World","VT","Danmaku","Atlas","Reuse","复用测试","弹幕","Shader","UV"
        };

        [Header("Medium Pool")]
        [Tooltip("How many medium-frequency labels to generate (Msg_00..)")]
        public int mediumCount = 30;
        public string mediumPrefix = "Msg_";

        [Header("Placement")]
        public Transform container;
        [Tooltip("Number of Y lanes for simple separation.")]
        public int lanes = 8;
        public float laneSpacing = 0.4f;
        public float xStart = -8f;
        public float xStep = 0.6f;
        [Header("Motion")]
        [Tooltip("Horizontal speed (units/sec). Negative moves left.")]
        public float moveSpeed = -3f;

        private VTTextAtlasShelf _atlas;
        private VTTextBatchRenderer _batch;
        private TextBakeParamsNative _bakeParams;

        private System.Random _rng;
        private float _accum = -10;
        private int _spawnIndex;

        private struct Active
        {
            public int handle;
            public Hash128 key;
            public Vector2Int pxSize;
            public Vector3 pos;
            public float ttl;
        }

        private readonly System.Collections.Generic.List<Active> _active = new System.Collections.Generic.List<Active>(256);
        private readonly System.Collections.Generic.List<string> _medium = new System.Collections.Generic.List<string>(64);
        private readonly System.Collections.Generic.List<string> _rarePool = new System.Collections.Generic.List<string>(256);
        private int _rareCounter;

        void Awake()
        {
            _atlas = GetComponent<VTTextAtlasShelf>();
            _batch = GetComponent<VTTextBatchRenderer>();

            if (!container)
            {
                var go = new GameObject("LargeScene_Texts_Batched");
                go.transform.SetParent(transform, false);
                container = go.transform;
            }

            if (!font && TMP_Settings.instance)
            {
                font = TMP_Settings.defaultFontAsset;
            }
        }

        void Start()
        {
            if (!_atlas || !_batch)
            {
                Debug.LogError("LargeSceneBootstrap_Batched: atlas or batch missing.");
                enabled = false; return;
            }
            if (!font)
            {
                Debug.LogError("LargeSceneBootstrap_Batched: TMP FontAsset not assigned.");
                enabled = false; return;
            }

            _bakeParams = new TextBakeParamsNative
            {
                font = font,
                pixelHeight = pixelHeight,
                faceColor = (Color32)faceColor,
                outlineColor = (Color32)outlineColor,
                outlineWidth = outlineWidth,
                richText = richText,
                padding = padding
            };

            _rng = (randomSeed > 0) ? new System.Random(randomSeed) : new System.Random();
            BuildMediumPool();

            // Hook atlas RT to batch material
            _batch.pixelsPerUnit = pixelsPerUnit;
            _batch.SetAtlasTexture(_atlas.GetAtlasRT());
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _accum += spawnRate * dt;

            int toSpawn = Mathf.FloorToInt(_accum);
            if (toSpawn > 0) _accum -= toSpawn;

            for (int i = 0; i < toSpawn; i++)
            {
                if (maxActive > 0 && _active.Count >= maxActive) break;
                SpawnOne();
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i];
                a.ttl -= dt;
                a.pos.x += moveSpeed * dt;

                // Update transform in batch
                _batch.UpdateTransform(a.handle, a.pos, a.pxSize, 0f, a.pos.z);

                if (a.ttl <= 0f)
                {
                    _batch.RemoveInstance(a.handle);
                    _atlas.Release(a.key);
                    _active.RemoveAt(i);
                }
                else
                {
                    _active[i] = a;
                }
            }
        }

        void BuildMediumPool()
        {
            _medium.Clear();
            int count = Mathf.Max(0, mediumCount);
            for (int i = 0; i < count; i++)
                _medium.Add($"{mediumPrefix}{i:00}");
        }

        string NextText()
        {
            int maxUnique = Mathf.Max(1, maxUniqueTexts);
            int commonAvail = (commonTexts != null) ? commonTexts.Length : 0;
            int commonLimit = Mathf.Min(commonAvail, maxUnique);
            int mediumLimit = Mathf.Min(_medium.Count, Mathf.Max(0, maxUnique - commonLimit));
            int allowedRare = Mathf.Max(0, maxUnique - (commonLimit + mediumLimit));

            int total = Mathf.Max(1, commonWeight + mediumWeight + rareWeight);
            int p = _rng.Next(0, total);

            if (p < commonWeight && commonLimit > 0)
                return commonTexts[_rng.Next(0, commonLimit)];
            p -= commonWeight;

            if (p < mediumWeight && mediumLimit > 0)
                return _medium[_rng.Next(0, mediumLimit)];
            p -= mediumWeight;

            if (allowedRare > 0)
            {
                if (_rarePool.Count < allowedRare)
                {
                    string r = $"R_{_rareCounter++:0000}";
                    _rarePool.Add(r);
                    return r;
                }
                else if (_rarePool.Count > 0)
                {
                    return _rarePool[_rng.Next(0, _rarePool.Count)];
                }
            }

            if (commonLimit > 0) return commonTexts[_rng.Next(0, commonLimit)];
            if (mediumLimit > 0) return _medium[_rng.Next(0, mediumLimit)];
            return "VT";
        }

        void SpawnOne()
        {
            string txt = NextText();
            Vector3 pos = new Vector3(
                xStart + (_spawnIndex % 32) * xStep,
                -((_spawnIndex % Mathf.Max(1, lanes)) * laneSpacing),
                0f);
            pos = container.TransformPoint(pos);

            // Bake or hit cache
            var uv = _atlas.GetOrBake(txt, _bakeParams);
            // Instance add
            int handle = _batch.AddInstance(
                uv.atlasRect01, uv.pixelSize, pos,
                Color.white, 0f, new Vector2(0.5f, 0.5f), pos.z);

            float lifeMin = Mathf.Min(lifetimeRange.x, lifetimeRange.y);
            float lifeMax = Mathf.Max(lifetimeRange.x, lifetimeRange.y);
            float ttl = lifeMin + (float)_rng.NextDouble() * Mathf.Max(0.0001f, (lifeMax - lifeMin));

            var key = VTTextAtlasShelf.MakeKey(txt, _bakeParams, _atlas.shelfHeightStep);

            _active.Add(new Active { handle = handle, key = key, pxSize = uv.pixelSize, pos = pos, ttl = ttl });
            _spawnIndex++;
        }
    }
}
