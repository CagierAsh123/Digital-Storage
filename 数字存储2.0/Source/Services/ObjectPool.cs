using System;
using System.Collections.Generic;

namespace DigitalStorage.Services
{
    /// <summary>
    /// 泛型对象池：减少 GC 压力
    /// </summary>
    public static class ObjectPool<T> where T : class, new()
    {
        private static Stack<T> pool = new Stack<T>(16);
        private static int maxPoolSize = 32;

        public static T Get()
        {
            if (pool.Count > 0)
            {
                return pool.Pop();
            }
            return new T();
        }

        public static void Return(T obj)
        {
            if (obj == null || pool.Count >= maxPoolSize)
            {
                return;
            }

            // 清理对象
            if (obj is System.Collections.ICollection collection)
            {
                collection.Clear();
            }

            pool.Push(obj);
        }
    }

    /// <summary>
    /// Dictionary 对象池
    /// </summary>
    public static class DictionaryPool<TKey, TValue>
    {
        private static Stack<Dictionary<TKey, TValue>> pool = new Stack<Dictionary<TKey, TValue>>(8);
        private static int maxPoolSize = 16;

        public static Dictionary<TKey, TValue> Get()
        {
            if (pool.Count > 0)
            {
                return pool.Pop();
            }
            return new Dictionary<TKey, TValue>();
        }

        public static void Return(Dictionary<TKey, TValue> dict)
        {
            if (dict == null || pool.Count >= maxPoolSize)
            {
                return;
            }

            dict.Clear();
            pool.Push(dict);
        }
    }

    /// <summary>
    /// List 对象池
    /// </summary>
    public static class ListPool<T>
    {
        private static Stack<List<T>> pool = new Stack<List<T>>(8);
        private static int maxPoolSize = 16;

        public static List<T> Get()
        {
            if (pool.Count > 0)
            {
                return pool.Pop();
            }
            return new List<T>();
        }

        public static void Return(List<T> list)
        {
            if (list == null || pool.Count >= maxPoolSize)
            {
                return;
            }

            list.Clear();
            pool.Push(list);
        }
    }

    /// <summary>
    /// HashSet 对象池
    /// </summary>
    public static class HashSetPool<T>
    {
        private static Stack<HashSet<T>> pool = new Stack<HashSet<T>>(8);
        private static int maxPoolSize = 16;

        public static HashSet<T> Get()
        {
            if (pool.Count > 0)
            {
                return pool.Pop();
            }
            return new HashSet<T>();
        }

        public static void Return(HashSet<T> set)
        {
            if (set == null || pool.Count >= maxPoolSize)
            {
                return;
            }

            set.Clear();
            pool.Push(set);
        }
    }
}
