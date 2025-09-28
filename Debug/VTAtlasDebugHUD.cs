using UnityEngine;

namespace Renderloom
{
    public class VTAtlasDebugHUD : MonoBehaviour
    {
        public VTTextAtlasShelf atlas;
        public Vector2 anchor = new Vector2(20, 20);

#if UNITY_EDITOR
        void OnGUI()
        {
            if (!atlas) return;
            var s = atlas.DebugGetStats();
            GUI.Label(new Rect(anchor.x, anchor.y, 520, 200),
                $"VT Shelf Atlas Stats\n" +
                $"- Entries Alive : {s.activeEntries}\n" +
                $"- Used Inner    : {s.usedAreaInner}  ({s.usageInner:P1})\n" +
                $"- Used WithPad  : {s.usedAreaWithPad} ({s.usageWithPad:P1})\n" +
                $"- EvictHeap     : {s.evictHeapCount}\n");
        }
#else
        void OnGUI() {}
#endif
    }
}
