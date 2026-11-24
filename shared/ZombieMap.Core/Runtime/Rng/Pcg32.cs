namespace ZombieMap.Core.Rng
{
    /// <summary>
    /// Small deterministic random generator (PCG32). Same seed gives the same sequence on
    /// every platform, and its whole state is two public fields, so it serializes with the
    /// encounter and a saved encounter continues exactly where it stopped.
    /// </summary>
    public sealed class Pcg32
    {
        private const ulong Multiplier = 6364136223846793005UL;

        public ulong State;
        public ulong Increment;

        /// <summary>For serializers only. Use the seeded constructor.</summary>
        public Pcg32() : this(0) { }

        public Pcg32(ulong seed, ulong sequence = 54)
        {
            State = 0;
            Increment = (sequence << 1) | 1UL;
            NextUInt();
            State += seed;
            NextUInt();
        }

        public uint NextUInt()
        {
            ulong old = State;
            State = unchecked(old * Multiplier + Increment);
            uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
        }

        /// <summary>Uniform integer in [minInclusive, maxExclusive), without modulo bias.</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint bound = (uint)(maxExclusive - minInclusive);
            uint threshold = (uint)((0x100000000UL - bound) % bound);
            while (true)
            {
                uint r = NextUInt();
                if (r >= threshold) return minInclusive + (int)(r % bound);
            }
        }

        /// <summary>Uniform double in [0, 1).</summary>
        public double NextDouble() => NextUInt() / 4294967296.0;

        /// <summary>Uniform double in [min, max).</summary>
        public double Range(double min, double max) => min + (max - min) * NextDouble();

        /// <summary>True with probability p (clamped to 0..1).</summary>
        public bool Chance(double p) => NextDouble() < p;

        public Pcg32 Clone() => new Pcg32 { State = State, Increment = Increment };
    }
}
