using System.Collections.Generic;

namespace ZombieMap.Core.Health
{
    /// <summary>The 17 body zones that wounds and clothing coverage are tracked on.</summary>
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

    /// <summary>Summary of a wound's care, used by encounter rules and shown to the player.</summary>
    public enum WoundTreatment
    {
        /// <summary>Open wound.</summary>
        None,
        /// <summary>Bandaged but not disinfected. Infection risk stays.</summary>
        Patched,
        /// <summary>Disinfected and bandaged.</summary>
        Treated,
    }

    public enum BandageKind { None, Rag, Bandage }

    public sealed class Wound
    {
        /// <summary>Unique per body. 0 = not assigned yet (made inside an encounter).</summary>
        public int Id;
        public BodyPart Part;
        public WoundKind Kind;
        public bool Bleeding;

        public BandageKind Bandage;
        /// <summary>Unix seconds the current bandage went on (it gets dirty with time).</summary>
        public long BandagedAt;
        public bool Disinfected;
        /// <summary>Lacerations only: stitched with a suture needle (heals twice as fast).</summary>
        public bool Stitched;
        /// <summary>Wound infection (dirt, not the zombie virus). Stops healing and drains blood.</summary>
        public bool Infected;

        /// <summary>Unix seconds the wound happened.</summary>
        public long CreatedAt;
        /// <summary>0..1. The wound is gone at 1.</summary>
        public double Healed;
        /// <summary>Seconds it has been open (not disinfected) without infection; reaching the limit infects it.</summary>
        public double DirtySeconds;

        /// <summary>
        /// Server-only truth: whether this wound carried the zombie virus. Never sent to the
        /// client (see WoundView); the player finds out from symptoms, like in Zomboid.
        /// </summary>
        public bool CarriesVirus;

        public WoundTreatment Treatment =>
            Bandage == BandageKind.None ? WoundTreatment.None : Disinfected ? WoundTreatment.Treated : WoundTreatment.Patched;

        public Wound Clone() => (Wound)MemberwiseClone();
    }

    public static class BodyParts
    {
        public static bool IsLeg(BodyPart p) =>
            p == BodyPart.LeftThigh || p == BodyPart.RightThigh ||
            p == BodyPart.LeftShin || p == BodyPart.RightShin ||
            p == BodyPart.LeftFoot || p == BodyPart.RightFoot;

        public static bool IsArm(BodyPart p) =>
            p == BodyPart.LeftUpperArm || p == BodyPart.RightUpperArm ||
            p == BodyPart.LeftForearm || p == BodyPart.RightForearm ||
            p == BodyPart.LeftHand || p == BodyPart.RightHand;

        /// <summary>"left forearm" style name.</summary>
        public static string Pretty(BodyPart p)
        {
            var name = p.ToString();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            return sb.ToString();
        }

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
