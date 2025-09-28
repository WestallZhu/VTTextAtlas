using UnityEngine;

#if UNITY_EDITOR
using Unity.Collections;
#endif

namespace Renderloom
{
    [ExecuteAlways]
    public class VTAtlasDebugOverlay : MonoBehaviour
    {
        public VTTextAtlasShelf atlas;         // 指向场景里的实例
        public Vector2 worldSize = new Vector2(2f, 2f); // 显示宽高（世界单位）
        public Vector3 worldOrigin = Vector3.zero;      // 左下角起点
        public bool drawFree = true;
        public bool drawUsed = true;
        public Color freeColor = new Color(0f, 1f, 0f, 0.25f);
        public Color usedColor = new Color(1f, 0f, 0f, 0.25f);
        public Color borderColor = new Color(0f, 0f, 0f, 0.8f);

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!atlas) return;

            if (!Application.isPlaying)
                return;

            var freeRects = new NativeList<NativeIntRect>(Allocator.Temp);
            var usedRects = new NativeList<NativeIntRect>(Allocator.Temp);

            atlas.DebugGetRects(ref freeRects, ref usedRects, out int texW, out int texH);

            DrawRectOutline(worldOrigin, worldSize, borderColor);

            float sx = worldSize.x / texW;
            float sy = worldSize.y / texH;

            if (drawFree)
            {
                for (int i = 0; i < freeRects.Length; i++)
                {
                    var r = freeRects[i];
                    DrawRectFill(
                        new Vector3(worldOrigin.x + r.x * sx, worldOrigin.y + r.y * sy, worldOrigin.z),
                        new Vector2(r.w * sx, r.h * sy),
                        freeColor);
                }
            }
            if (drawUsed)
            {
                for (int i = 0; i < usedRects.Length; i++)
                {
                    var r = usedRects[i];
                    DrawRectFill(
                        new Vector3(worldOrigin.x + r.x * sx, worldOrigin.y + r.y * sy, worldOrigin.z),
                        new Vector2(r.w * sx, r.h * sy),
                        usedColor);
                }
            }

            freeRects.Dispose();
            usedRects.Dispose();
        }

        static void DrawRectOutline(Vector3 origin, Vector2 size, Color col)
        {
            Gizmos.color = col;
            var a = origin;
            var b = origin + new Vector3(size.x, 0, 0);
            var c = origin + new Vector3(size.x, size.y, 0);
            var d = origin + new Vector3(0, size.y, 0);
            Gizmos.DrawLine(a,b); Gizmos.DrawLine(b,c);
            Gizmos.DrawLine(c,d); Gizmos.DrawLine(d,a);
        }

        static void DrawRectFill(Vector3 origin, Vector2 size, Color col)
        {
            Gizmos.color = col;
            var center = origin + new Vector3(size.x * 0.5f, size.y * 0.5f, 0);
            Gizmos.DrawCube(center, new Vector3(size.x, size.y, 0.0001f));
        }
#else
        void OnDrawGizmos() {}
#endif
    }
}
