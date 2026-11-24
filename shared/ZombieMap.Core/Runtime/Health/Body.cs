using System.Collections.Generic;

namespace ZombieMap.Core.Health
{
    /// <summary>The 17 body zones that wounds (and later clothing coverage) are tracked on.</summary>
    public enum BodyPart
    {
        Head,
        Neck,
        UpperTorso,
        LowerTorso,
        Groin,
        LeftUpperArm,
        RightUpperArm,
        LeftForearm,
        RightForearm,
        LeftHand,
        RightHand,
        LeftThigh,
        RightThigh,
        LeftShin,
        RightShin,
        LeftFoot,
        RightFoot,
    }

    public enum WoundKind
    {
        Scratch,
        Laceration,
        Bite,
    }

    public enum WoundTreatment
    {
        /// <summary>Open wound.</summary>
        None,
        /// <summary>Bleeding stopped with a rag or bandage, not disinfected. Infection risk stays.</summary>
        Patched,
        /// <summary>Disinfected and clean-bandaged.</summary>
        Treated,
    }

    public sealed class Wound
    {
        public BodyPart Part;
        public WoundKind Kind;
        public bool Bleeding;
        public WoundTreatment Treatment;

        /// <summary>
        /// Server-only truth: whether this wound carried the zombie virus. Never sent to the
        /// client (see EncounterView); the player finds out from symptoms, like in Zomboid.
        /// </summary>
        public bool CarriesVirus;
    }

    public static class BodyParts
    {
        public static bool IsLeg(BodyPart p) =>
            p == BodyPart.LeftThigh || p == BodyPart.RightThigh ||
            p == BodyPart.LeftShin || p == BodyPart.RightShin ||
            p == BodyPart.LeftFoot || p == BodyPart.RightFoot;

        /// <summary>
        /// Where a grabbing zombie tends to land: hands, forearms, neck and head
        /// (Zomboid-like weighting). Weights are relative.
        /// </summary>
        public static readonly IReadOnlyList<KeyValuePair<BodyPart, int>> GrabHitWeights = new[]
        {
            new KeyValuePair<BodyPart, int>(BodyPart.LeftHand, 12),
            new KeyValuePair<BodyPart, int>(BodyPart.RightHand, 12),
            new KeyValuePair<BodyPart, int>(BodyPart.LeftForearm, 14),
            new KeyValuePair<BodyPart, int>(BodyPart.RightForearm, 14),
            new KeyValuePair<BodyPart, int>(BodyPart.LeftUpperArm, 6),
            new KeyValuePair<BodyPart, int>(BodyPart.RightUpperArm, 6),
            new KeyValuePair<BodyPart, int>(BodyPart.Neck, 8),
            new KeyValuePair<BodyPart, int>(BodyPart.Head, 5),
            new KeyValuePair<BodyPart, int>(BodyPart.UpperTorso, 9),
            new KeyValuePair<BodyPart, int>(BodyPart.LowerTorso, 5),
            new KeyValuePair<BodyPart, int>(BodyPart.LeftShin, 3),
            new KeyValuePair<BodyPart, int>(BodyPart.RightShin, 3),
        };
    }
}
