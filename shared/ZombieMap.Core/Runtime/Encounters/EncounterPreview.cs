using System.Collections.Generic;

namespace ZombieMap.Core.Encounters
{
    /// <summary>
    /// Client-side "ghost" preview: where the zombies you can see will be after N ticks,
    /// if nothing else happens. Uses only what's in the EncounterView, so it works on the
    /// client with no hidden state. Silhouettes and sounds can't be predicted.
    /// </summary>
    public static class EncounterPreview
    {
        public static List<ZombieView> Predict(EncounterView view, int ticks)
        {
            var result = new List<ZombieView>();
            foreach (var z in view.Zombies)
            {
                if (z.Silhouette || z.Kind == null || z.TicksUntilMove == null) continue;
                int ring = z.Ring;
                int down = z.KnockedDownTicks;
                int untilMove = z.TicksUntilMove.Value;
                int perRing = Rules.TicksPerRing(z.Kind.Value);
                for (int t = 0; t < ticks; t++)
                {
                    if (down > 0) { down--; continue; }
                    if (ring == 0) continue;
                    untilMove--;
                    if (untilMove <= 0) { ring--; untilMove = perRing; }
                }
                result.Add(new ZombieView
                {
                    Id = z.Id, Lane = z.Lane, Ring = ring, Kind = z.Kind,
                    TicksUntilMove = untilMove, KnockedDownTicks = down,
                });
            }
            return result;
        }

        /// <summary>Ticks until this zombie reaches you (ring 0), or null if unknown.</summary>
        public static int? TicksUntilContact(ZombieView z)
        {
            if (z.Silhouette || z.Kind == null || z.TicksUntilMove == null) return null;
            if (z.Ring == 0) return 0;
            return z.KnockedDownTicks + z.TicksUntilMove.Value + (z.Ring - 1) * Rules.TicksPerRing(z.Kind.Value);
        }
    }
}
