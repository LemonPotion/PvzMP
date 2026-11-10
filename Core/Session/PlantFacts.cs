namespace PvzMP.Core.Session
{
    /// <summary>
    /// The part of a plant that outlives a single frame: what it has left to live, how far it has grown,
    /// how it looks, and the counters and flags its behaviour reads. The cell and the kind travel in the
    /// same layout entry; these are the numbers around them. Only the host's copy is ever sent.
    /// Proof: ref/EVIDENCE.md E72.
    /// </summary>
    public struct PlantFacts
    {
        public int Health;
        public int MaxHealth;
        public int Shield;
        public int Level;
        public int Stage;
        public int Skin;
        public int Magnet;
        public int Wheat;
        public int ShootLevel;
        public int ShootCurse;
        public int Kills;
        public int Puff;
        public int Butter;
        public int Attribute;
        public int Disable;
        public int Freeze;
        public int FirstParent;
        public int SecondParent;
        public int LilyType;

        /// <summary>Booleans packed by <see cref="PlantFactsRules"/>.</summary>
        public int Flags;

        /// <summary>Two plants whose every number matches need no correction at all.</summary>
        public bool SameAs(PlantFacts other)
        {
            return Health == other.Health && MaxHealth == other.MaxHealth && Shield == other.Shield
                && Level == other.Level && Stage == other.Stage && Skin == other.Skin
                && Magnet == other.Magnet && Wheat == other.Wheat
                && ShootLevel == other.ShootLevel && ShootCurse == other.ShootCurse
                && Kills == other.Kills && Puff == other.Puff && Butter == other.Butter
                && Attribute == other.Attribute && Disable == other.Disable
                && Freeze == other.Freeze && FirstParent == other.FirstParent
                && SecondParent == other.SecondParent && LilyType == other.LilyType
                && Flags == other.Flags;
        }
    }

    /// <summary>What a plant's health has to do to look like the host's plant.</summary>
    public enum HealthStep
    {
        None = 0,
        Heal = 1,
        Damage = 2
    }

    /// <summary>
    /// The room's rules for plant facts, kept free of the game so they are tested as they ship.
    ///
    /// Two rules matter. Health is matched to the host's number through the game's own heal and damage
    /// doors, and the amount is the difference the two lawns disagree by, never the whole bar. Death is
    /// the host's sentence: a peer's own game may bring its plant to zero, but the plant stays up until
    /// the host's layout stops naming it, which is the removal that runs the game's own `Die`.
    /// Proof: ref/EVIDENCE.md E72, E73.
    /// </summary>
    public static class PlantFactsRules
    {
        public const int Invincible = 1;
        public const int Undead = 2;
        public const int IsLily = 4;
        public const int Imitatless = 8;
        public const int KeepShooting = 16;
        public const int Uncrashable = 32;
        public const int MelonSputter = 64;

        public static int Pack(bool invincible, bool undead, bool isLily, bool imitatless,
            bool keepShooting, bool uncrashable, bool melonSputter)
        {
            int flags = 0;

            if (invincible)
            {
                flags |= Invincible;
            }

            if (undead)
            {
                flags |= Undead;
            }

            if (isLily)
            {
                flags |= IsLily;
            }

            if (imitatless)
            {
                flags |= Imitatless;
            }

            if (keepShooting)
            {
                flags |= KeepShooting;
            }

            if (uncrashable)
            {
                flags |= Uncrashable;
            }

            if (melonSputter)
            {
                flags |= MelonSputter;
            }

            return flags;
        }

        public static bool Has(int flags, int bit)
        {
            return (flags & bit) == bit;
        }

        /// <summary>
        /// The health the game's own door has to put back or take away, so this lawn shows the host's
        /// number. A host number of zero or below never arrives: a dead plant is an entry the layout
        /// does not name, and its removal is the death.
        /// </summary>
        public static HealthStep Between(int here, int wanted, out int amount)
        {
            amount = 0;

            if (wanted <= 0)
            {
                return HealthStep.None;
            }

            if (wanted > here)
            {
                amount = wanted - here;
                return HealthStep.Heal;
            }

            if (wanted < here)
            {
                amount = here - wanted;
                return HealthStep.Damage;
            }

            return HealthStep.None;
        }

        /// <summary>
        /// Whether the game's `Plant.Die` may run here. The host says yes to its own lawn; a peer says yes
        /// only when the mod itself is carrying out the removal, or when the plant is not a room plant at
        /// all. A room plant on a peer waits for the host's layout instead of dying on a local roll.
        /// Proof: ref/EVIDENCE.md E73.
        /// </summary>
        public static bool MayDie(bool roomDecides, bool inLevel, bool isHost, bool byTheMod,
            bool roomBound)
        {
            if (!roomDecides || !inLevel || isHost || byTheMod || !roomBound)
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The one mapping between a layout entry and the facts a lawn keeps, so the host's send and the
    /// peer's receive cannot disagree about which number is which.
    /// Proof: ref/EVIDENCE.md E72.
    /// </summary>
    public static class PlantSnapRules
    {
        public static PlantSnap Of(int netId, int plantType, int column, int row, PlantFacts facts)
        {
            return new PlantSnap
            {
                NetId = netId,
                PlantType = plantType,
                Column = column,
                Row = row,
                Health = facts.Health,
                MaxHealth = facts.MaxHealth,
                Shield = facts.Shield,
                Level = facts.Level,
                Stage = facts.Stage,
                Skin = facts.Skin,
                Magnet = facts.Magnet,
                Wheat = facts.Wheat,
                ShootLevel = facts.ShootLevel,
                ShootCurse = facts.ShootCurse,
                Kills = facts.Kills,
                Puff = facts.Puff,
                Butter = facts.Butter,
                Attribute = facts.Attribute,
                Disable = facts.Disable,
                Freeze = facts.Freeze,
                FirstParent = facts.FirstParent,
                SecondParent = facts.SecondParent,
                LilyType = facts.LilyType,
                Flags = facts.Flags
            };
        }

        public static PlantFacts FactsOf(PlantSnap snap)
        {
            PlantFacts facts = default(PlantFacts);

            if (snap == null)
            {
                return facts;
            }

            facts.Health = snap.Health;
            facts.MaxHealth = snap.MaxHealth;
            facts.Shield = snap.Shield;
            facts.Level = snap.Level;
            facts.Stage = snap.Stage;
            facts.Skin = snap.Skin;
            facts.Magnet = snap.Magnet;
            facts.Wheat = snap.Wheat;
            facts.ShootLevel = snap.ShootLevel;
            facts.ShootCurse = snap.ShootCurse;
            facts.Kills = snap.Kills;
            facts.Puff = snap.Puff;
            facts.Butter = snap.Butter;
            facts.Attribute = snap.Attribute;
            facts.Disable = snap.Disable;
            facts.Freeze = snap.Freeze;
            facts.FirstParent = snap.FirstParent;
            facts.SecondParent = snap.SecondParent;
            facts.LilyType = snap.LilyType;
            facts.Flags = snap.Flags;

            return facts;
        }
    }
}
