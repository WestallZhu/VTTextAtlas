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
        private NativeList<int2> _indexToEntity;  // arrayIndex -> entity(int2)
        private EntityIndexer _indexer;

        // GPU buffers (Constant Buffer for DIP)
        private ComputeBuffer _instanceBuffer;
        private Mesh _quad;
        private Bounds _bounds;
        private bool _buffersDirty = true;

        const int kFloat4Stride = 16; // bytes

        public int AliveCount => _instances.IsCreated ? _instances.Length : 0;

        void OnEnable()
        {
            if (!shader) shader = Shader.Find("VT/VTAtlas-InstancedUnlit");
            if (!material && shader)
            {
                material = new Material(shader) { name = "VT_Instanced_Text_Mat" };
                material.enableInstancing = true;
            }
            if (atlasTexture && material) material.SetTexture("_AtlasTex", atlasTexture);

            if (_indexer == null) _indexer = new EntityIndexer();

            if (!_instances.IsCreated) _instances = new NativeList<InstanceGPU>(initialCapacity, Allocator.Persistent);
            if (!_indexToEntity.IsCreated) _indexToEntity = new NativeList<int2>(initialCapacity, Allocator.Persistent);

            if (_quad == null) _quad = BuildUnitQuad();
            CreateOrResizeBuffers(math.max(1, initialCapacity));

            _bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
        }

        void OnDisable()
        {
            if (_instanceBuffer != null) { _instanceBuffer.Release(); _instanceBuffer = null; }

            if (_instances.IsCreated) _instances.Dispose();
            if (_indexToEntity.IsCreated) _indexToEntity.Dispose();

            if (_indexer != null) { _indexer.Dispose(); _indexer = null; }
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
            var idx = new int[] { 0, 1, 2, 0, 2, 3 };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetIndices(idx, MeshTopology.Triangles, 0, false);
            m.UploadMeshData(true);
            return m;
        }

        void CreateOrResizeBuffers(int capacityInstances)
        {
            capacityInstances = math.clamp(capacityInstances, 1, maxCapacity);
            int stride = Marshal.SizeOf<InstanceGPU>();   // 64 bytes / instance
            int float4Count = (stride * capacityInstances) / kFloat4Stride; // 4 * instances

            if (_instanceBuffer == null || _instanceBuffer.count < float4Count)
            {
                if (_instanceBuffer != null) _instanceBuffer.Release();
                _instanceBuffer = new ComputeBuffer(float4Count, kFloat4Stride, ComputeBufferType.Constant);

                if (material)
                {
                    int sizeBytes = stride * capacityInstances;
                    material.SetConstantBuffer("InstanceCBuffer", _instanceBuffer, 0, sizeBytes);
                }
            }
        }

        public void SetAtlasTexture(Texture tex)
        {
            atlasTexture = tex;
            if (material) material.SetTexture("_AtlasTex", atlasTexture);
        }


        public int2 AddInstance(Vector4 atlasRect01, Vector2 pixelSize, Vector3 worldPosPivot,
                                Color color, float rotationRad = 0f, Vector2? pivot01 = null, float orderZ = 0f)
        {
            var inst = new InstanceGPU();
            Vector2 sizeWorld = pixelSize / pixelsPerUnit;
            inst.posSize = new Vector4(worldPosPivot.x, worldPosPivot.y, sizeWorld.x, sizeWorld.y);
            inst.atlasRect = atlasRect01;
            inst.color = color;
            var pv = pivot01 ?? new Vector2(0.5f, 0.5f);
            inst.extra = new Vector4(pv.x, pv.y, rotationRad, orderZ);

            _instances.Add(inst);
            int arrayIdx = _instances.Length - 1;

            var entity = _indexer.CreateEntity(arrayIdx);
            _indexToEntity.Add(entity);

            if (_instanceBuffer == null || _instanceBuffer.count < _instances.Length * 4)
                CreateOrResizeBuffers(math.min(math.max(1, _instances.Length * 2), maxCapacity));

            _buffersDirty = true;
            return entity;
        }

        public bool RemoveInstance(int2 entity)
        {
            if (_indexer == null || !_indexer.IsValid(entity)) return false;

            int arrayIdx = _indexer.GetItem(entity).x;
            int last = _instances.Length - 1;

            if (arrayIdx != last)
            {
                // swap denseIdx
                var movedInst = _instances[last];
                var movedHandle = _indexToEntity[last];

                _instances[arrayIdx] = movedInst;
                _indexToEntity[arrayIdx] = movedHandle;

                // update  movedHandle 
                _indexer.UpdateIndex(movedHandle, arrayIdx);
            }

            _instances.RemoveAt(last);
            _indexToEntity.RemoveAt(last);

            _indexer.DestroyEntity(entity);

            _buffersDirty = true;
            return true;
        }

        public bool UpdateTransform(int2 entity, Vector3 worldPosPivot, Vector2 pixelSize, float rotationRad, float orderZ)
        {
            if (_indexer == null || !_indexer.IsValid(entity)) return false;
            int arrayIdx = _indexer.GetItem(entity).x;

            var inst = _instances[arrayIdx];
            Vector2 sizeWorld = pixelSize / pixelsPerUnit;
            inst.posSize = new Vector4(worldPosPivot.x, worldPosPivot.y, sizeWorld.x, sizeWorld.y);
            inst.extra.z = rotationRad;
            inst.extra.w = orderZ;
            _instances[arrayIdx] = inst;

            _buffersDirty = true;
            return true;
        }

        public bool UpdateColor(int2 entity, Color color)
        {
            if (_indexer == null || !_indexer.IsValid(entity)) return false;
            int arrayIdx = _indexer.GetItem(entity).x;

            var inst = _instances[arrayIdx];
            inst.color = color;
            _instances[arrayIdx] = inst;

            _buffersDirty = true;
            return true;
        }

        public bool UpdateUV(int2 entity, Vector4 atlasRect01, Vector2 pixelSize)
        {
            if (_indexer == null || !_indexer.IsValid(entity)) return false;
            int arrayIdx = _indexer.GetItem(entity).x;

            var inst = _instances[arrayIdx];
            inst.atlasRect = atlasRect01;
            Vector2 sizeWorld = pixelSize / pixelsPerUnit;
            inst.posSize.z = sizeWorld.x;
            inst.posSize.w = sizeWorld.y;
            _instances[arrayIdx] = inst;

            _buffersDirty = true;
            return true;
        }

        public bool IsValid(in int2 entity) => _indexer != null && _indexer.IsValid(entity);
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
