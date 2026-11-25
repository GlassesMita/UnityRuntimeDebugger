// netstandard2.0 compatibility shims for the BepInEx 6 Mono build.
//
// Core is written against .NET 6. The Mono host has to target netstandard2.0, because BepInEx 6 Mono
// loads plugins into the game's own Unity Mono runtime (mscorlib 4.0.0.0 / .NET Framework 4.x profile)
// rather than IL2CPP's .NET 6 runtime — a net6.0 plugin is listed by the chainloader and then dies in
// RunModuleConstructor with "Could not load file or assembly 'System.Runtime, Version=6.0.0.0'".
//
// PolySharp cannot help here: it injects compiler-level types (records, init, Range/Index pattern
// support) but no BCL *methods*. These are the few members Core actually calls that netstandard2.0
// does not declare, each a straight port of the .NET 6 implementation.
//
// The types are compiled unconditionally (they are plain code and harmless under net6.0); only the
// extension methods would be ambiguous there, so those stay behind the netstandard guard and Core
// calls the explicitly-qualified static form instead.
using System;
using System.Collections.Generic;

namespace UnityRuntimeDebugger.Compat
{
    public static class MathCompat
    {
        public static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        public static long Clamp(long value, long min, long max)
            => value < min ? min : (value > max ? max : value);

        public static float Clamp(float value, float min, float max)
            => value < min ? min : (value > max ? max : value);

        public static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);

        /// <summary>MathF.Round, which netstandard2.0 has no System.MathF for.</summary>
        public static float Round(float value) => (float)Math.Round(value);
    }

    public static class CollectionCompat
    {
        public static bool TryAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key))
                return false;
            dictionary.Add(key, value);
            return true;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
            => dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue fallback)
            => dictionary.TryGetValue(key, out var value) ? value : fallback;

        /// <summary>KeyValuePair deconstruction, which netstandard2.0 lacks.</summary>
        public static void Deconstruct<TKey, TValue>(KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    public static class EnvironmentCompat
    {
        /// <summary>netstandard2.0 has no Environment.TickCount64; widen the 32-bit counter.</summary>
        public static long TickCount64 => Environment.TickCount & 0xFFFFFFFFL;
    }
}

