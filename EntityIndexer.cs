using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Renderloom
{
    internal class EntityIndexer
    {
        private NativeList<int2> _entities = new NativeList<int2>(64, Allocator.Persistent);
        int _nextFreeEntityIndex = -1;

        public bool IsValid(in int2 entity)
        {
            if (_entities.Length <= entity.x)
                return false;

            return _entities[entity.x].y == entity.y;
        }

        public int2 CreateEntity(int arrayIndex)
        {
            // Reuse
            if (_nextFreeEntityIndex >= 0)
            {
                int entityIndex = _nextFreeEntityIndex;
                _nextFreeEntityIndex = _entities[entityIndex].x;
                int newVersion = _entities[entityIndex].x + 1;

                _entities[entityIndex] = new int2()
                {
                    x = arrayIndex,
                    y = newVersion 
                };

                return new int2()
                {
                    x = entityIndex,
                    y = newVersion
                };
            }

            // Create new one
            {
                int entityIndex = _entities.Length;
                int version = 1;

                _entities.Add(new int2()
                {
                    x = arrayIndex,
                    y = version,
                });


                return new int2()
                {
                    x = entityIndex,
                    y = version,
                };
            }
        }
        public void DestroyEntity(int2 entity)
        {
            int freeIndex = entity.x;

            var item = _entities[entity.x];
            item.x = _nextFreeEntityIndex;
            _nextFreeEntityIndex = entity.x;

            _entities[entity.x] = item;
        }
        public int2 GetItem(int2 entity)
        {
            return _entities[entity.x];
        }

        public void UpdateIndex(int2 entity, int newArrayIndex)
        {
            var item = _entities[entity.x];
            item.x = newArrayIndex;
            _entities[entity.x] = item;
        }
        public void Clear()
        {
            _entities.Clear();
            _nextFreeEntityIndex = -1;
        }

        public void Dispose()
        {
            _entities.Dispose();
        }
    }
}
