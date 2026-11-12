using System;
using System.Collections.Generic;
using System.Reflection;

namespace PersistentJobsRework
{
    /// <summary>
    /// Kleiner, gecachter Reflection-Zugriff auf private Felder/Properties des Spiels.
    /// Bewusst ohne AccessTools/Traverse: kein Log-Spam, wenn etwas fehlt.
    /// </summary>
    internal static class Reflect
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Dictionary<(Type, string), MemberInfo> Cache = new Dictionary<(Type, string), MemberInfo>();

        private static MemberInfo Find(Type type, string name)
        {
            var key = (type, name);
            if (Cache.TryGetValue(key, out var cached)) return cached;

            MemberInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
            {
                found = (MemberInfo)t.GetField(name, Flags | BindingFlags.DeclaredOnly)
                        ?? t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            }

            Cache[key] = found;
            return found;
        }

        public static bool Has(object obj, string name) => obj != null && Find(obj.GetType(), name) != null;

        public static T Get<T>(object obj, string name)
        {
            if (obj == null) return default;
            switch (Find(obj.GetType(), name))
            {
                case FieldInfo f: return f.GetValue(obj) is T tf ? tf : default;
                case PropertyInfo p when p.CanRead: return p.GetValue(obj, null) is T tp ? tp : default;
                default: return default;
            }
        }

        /// <summary>Erster vorhandener Name gewinnt (z. B. "state" oder "State").</summary>
        public static T GetAny<T>(object obj, params string[] names)
        {
            foreach (var n in names)
                if (Has(obj, n)) return Get<T>(obj, n);
            return default;
        }

        public static bool Set(object obj, string name, object value)
        {
            if (obj == null) return false;
            switch (Find(obj.GetType(), name))
            {
                case FieldInfo f: f.SetValue(obj, value); return true;
                case PropertyInfo p when p.CanWrite: p.SetValue(obj, value, null); return true;
                default: return false;
            }
        }

        public static MethodInfo Method(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(Flags | BindingFlags.DeclaredOnly))
                    if (m.Name == name) return m;
            }
            return null;
        }
    }
}
