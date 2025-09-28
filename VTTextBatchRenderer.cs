using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Renderloom
{
    [ExecuteAlways]
    public unsafe class VTTextBatchRenderer : MonoBehaviour
    {
        [Header("Material & Atlas")]
        public Shader shader;                      // VT/VTAtlas-InstancedUnlitIndirect
        public Material material;                  // if null will be created from shader
        public Texture  atlasTexture;              // set from VTTextAtlas_Native_Shelf.GetAtlasRT()

        [Header("Capacity")]
        public int initialCapacity = 256;
        public int maxCapacity     = 256;

        [Header("Pixels")]
        public float pixelsPerUnit = 100f;

        // -------- Instance data --------
        [StructLayout(LayoutKind.Sequential)]
        public struct InstanceGPU
        {
            public Vector4 posSize;   // pos.xy (pivot world), size.xy (world)
            public Vector4 atlasRect; // xy offset, zw size (0..1)
            public Color   color;     // rgba
            public Vector4 extra;     // pivot.xy (0..1), rotation(rad), orderZ
        }

        private NativeList<InstanceGPU> _instances;
        private NativeList<int>          _indexToHandle;     // index -> handle
        private NativeHashMap<int,int>   _handleToIndex;     // handle -> index
        private NativeList<int>          _freeHandles;
        private int                      _nextHandle = 1;

        // GPU buffers
        private ComputeBuffer _instanceBuffer;
        private Mesh          _quad;
        private Bounds        _bounds;
        private bool          _buffersDirty = true;

        public int AliveCount => _instances.IsCreated ? _instances.Length : 0;

        void OnEnable()
        {
            if (!shader)
                shader = Shader.Find("VT/VTAtlas-InstancedUnlit");
            if (!material && shader)
            {
                material = new Material(shader) { name = "VT_Instanced_Text_Mat" };
                material.enableInstancing = true;
            }
            if (atlasTexture && material) material.SetTexture("_AtlasTex", atlasTexture);

            if (!_instances.IsCreated)
            {
                _instances      = new NativeList<InstanceGPU>(initialCapacity, Allocator.Persistent);
                _indexToHandle  = new NativeList<int>(initialCapacity, Allocator.Persistent);
                _handleToIndex  = new NativeHashMap<int,int>(initialCapacity, Allocator.Persistent);
                _freeHandles    = new NativeList<int>(Allocator.Persistent);
            }

            if (_quad == null) _quad = BuildUnitQuad();
            CreateOrResizeBuffers(math.max(1, initialCapacity));

            _bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
        }

        void OnDisable()
        {
            if (_instanceBuffer != null) { _instanceBuffer.Release(); _instanceBuffer = null; }
        }

        void OnDestroy()
        {
            if (_instanceBuffer != null) { _instanceBuffer.Release(); _instanceBuffer = null; }
            if (_instances.IsCreated)      _instances.Dispose();
            if (_indexToHandle.IsCreated)  _indexToHandle.Dispose();
            if (_handleToIndex.IsCreated)  _handleToIndex.Dispose();
            if (_freeHandles.IsCreated)    _freeHandles.Dispose();
        }

        Mesh BuildUnitQuad()
        {
            var m = new Mesh { name = "VT_UnitQuad" };
            var v = new Vector3[] {
                new Vector3(0,0,0), new Vector3(1,0,0),
                new Vector3(1,1,0), new Vector3(0,1,0)
            };
            var uv = new Vector2[] {
                new Vector2(0,0), new Vector2(1,0),
                new Vector2(1,1), new Vector2(0,1)
            };
            var idx = new int[] { 0,1,2, 0,2,3 };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetIndices(idx, MeshTopology.Triangles, 0, false);
            m.UploadMeshData(true);
            return m;
        }

        const int kFloat4Stride = 16;
        void CreateOrResizeBuffers(int capacity)
        {
            capacity = math.clamp(capacity, 1, maxCapacity);
            int stride = Marshal.SizeOf<InstanceGPU>();

            if (_instanceBuffer == null || _instanceBuffer.count < capacity)
            {
                if (_instanceBuffer != null) _instanceBuffer.Release();
                int size = stride * capacity;
                _instanceBuffer = new ComputeBuffer(size / kFloat4Stride, kFloat4Stride, ComputeBufferType.Constant);
                if (material) material.SetConstantBuffer("InstanceCBuffer", _instanceBuffer, 0, size);
            }
        }

        public void SetAtlasTexture(Texture tex)
        {
            atlasTexture = tex;
            if (material) material.SetTexture("_AtlasTex", atlasTexture);
        }

        public int AddInstance(Vector4 atlasRect01, Vector2 pixelSize, Vector3 worldPosPivot,
                               Color color, float rotationRad = 0f, Vector2? pivot01 = null, float orderZ = 0f)
        {
            var inst = new InstanceGPU();
            Vector2 sizeWorld = pixelSize / pixelsPerUnit;
            inst.posSize   = new Vector4(worldPosPivot.x, worldPosPivot.y, sizeWorld.x, sizeWorld.y);
            inst.atlasRect = atlasRect01;
            inst.color     = color;
            var pv = pivot01 ?? new Vector2(0.5f, 0.5f);
            inst.extra     = new Vector4(pv.x, pv.y, rotationRad, orderZ);

            if (_instanceBuffer == null || _instances.Length == _instanceBuffer.count)
                CreateOrResizeBuffers(math.min(math.max(1, (_instanceBuffer != null ? _instanceBuffer.count : 0) * 2), maxCapacity));

            _instances.Add(inst);
            int idx = _instances.Length - 1;

            int handle = (_freeHandles.Length > 0) ? _freeHandles[_freeHandles.Length - 1] : _nextHandle++;
            if (_freeHandles.Length > 0) _freeHandles.RemoveAtSwapBack(_freeHandles.Length - 1);
            if (handle == 0) handle = _nextHandle++;
            _indexToHandle.Add(handle);
            _handleToIndex.TryAdd(handle, idx);

            _buffersDirty = true;
            return handle;
        }

        public bool RemoveInstance(int handle)
        {
            if (!_handleToIndex.TryGetValue(handle, out int idx)) return false;
            int last = _instances.Length - 1;

            if (idx != last)
            {
                var moved = _instances[last];
                _instances[idx] = moved;

                int movedHandle = _indexToHandle[last];
                _indexToHandle[idx] = movedHandle;
                _handleToIndex[movedHandle] = idx;
            }

            _instances.RemoveAtSwapBack(idx);
            _indexToHandle.RemoveAtSwapBack(idx);
            _handleToIndex.Remove(handle);
            _freeHandles.Add(handle);

            _buffersDirty = true;
            return true;
        }

        public bool UpdateTransform(int handle, Vector3 worldPosPivot, Vector2 pixelSize, float rotationRad, float orderZ)
        {
            if (!_handleToIndex.TryGetValue(handle, out int idx)) return false;
            var inst = _instances[idx];
            Vector2 sizeWorld = pixelSize / pixelsPerUnit;
            inst.posSize = new Vector4(worldPosPivot.x, worldPosPivot.y, sizeWorld.x, sizeWorld.y);
            inst.extra.z = rotationRad;
            inst.extra.w = orderZ;
            _instances[idx] = inst;
            _buffersDirty = true;
            return true;
        }

        public bool UpdateColor(int handle, Color color)
        {
            if (!_handleToIndex.TryGetValue(handle, out int idx)) return false;
            var inst = _instances[idx];
            inst.color = color;
            _instances[idx] = inst;
            _buffersDirty = true;
            return true;
        }

        public bool UpdateUV(int handle, Vector4 atlasRect01, Vector2 pixelSize)
        {
            if (!_handleToIndex.TryGetValue(handle, out int idx)) return false;
            var inst = _instances[idx];
            inst.atlasRect = atlasRect01;
            Vector2 sizeWorld = pixelSize / pixelsPerUnit;
            inst.posSize.z = sizeWorld.x;
            inst.posSize.w = sizeWorld.y;
            _instances[idx] = inst;
            _buffersDirty = true;
            return true;
        }

        void LateUpdate()
        {
            int count = _instances.IsCreated ? _instances.Length : 0;
            if (count <= 0 || material == null) return;

            if (_buffersDirty)
            {
                UploadInstances(count);
                _buffersDirty = false;
            }

            if (atlasTexture) material.SetTexture("_AtlasTex", atlasTexture);

            Graphics.DrawMeshInstancedProcedural(
                _quad, 0, material, _bounds, count);
        }

        void UploadInstances(int count)
        {
            var data = _instances.AsArray().Reinterpret<float4>(UnsafeUtility.SizeOf<InstanceGPU>());
            _instanceBuffer.SetData(data, 0, 0, count * 4); 
            int sizeBytes = math.min(count * 4, _instanceBuffer.count) * kFloat4Stride;
            material.SetConstantBuffer("InstanceCBuffer", _instanceBuffer, 0, sizeBytes);
        }
    }
}
