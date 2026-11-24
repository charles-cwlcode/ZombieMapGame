namespace ZombieMap.Core.Rng
{
    /// <summary>
    /// FNV-1a 64-bit hash. Unlike string.GetHashCode it never changes between runs,
    /// processes or platforms, so it is safe for seeding (e.g. building ID → floor plan).
    /// </summary>
    public static class StableHash
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Hash64(string text)
        {
            ulong hash = Offset;
            if (text == null) return hash;
            unchecked
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    hash ^= (byte)(c & 0xFF);
                    hash *= Prime;
                    hash ^= (byte)(c >> 8);
                    hash *= Prime;
                }
            }
            return hash;
        }

        public static ulong Combine(ulong a, ulong b)
        {
            unchecked
            {
                ulong h = a ^ (b + 0x9E3779B97F4A7C15UL + (a << 6) + (a >> 2));
                return h;
            }
        }
    }
}
