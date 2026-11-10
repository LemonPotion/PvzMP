using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// One damage step the game's own crush applied: what it hit and with what numbers. The room repeats
    /// the step, not the tool, so everything the native body read from its call site has to travel with
    /// the blow or the other lawns compute a different hit.
    /// </summary>
    public sealed class HammerBlow
    {
        /// <summary>This machine's id of the zombie the step landed on.</summary>
        public int InstanceId;

        /// <summary>The damage the game computed for this zombie.</summary>
        public int Damage;

        /// <summary>The simple class name of the maker the game passed, or null when there was none.</summary>
        public string Maker;

        /// <summary>`DamageType` as the native body read it.</summary>
        public int DamageType;

        /// <summary>The plant type the damage reports itself as.</summary>
        public int ReportType;

        /// <summary>Whether the game told the damage step to keep the zombie alive at one hit point.</summary>
        public bool Fix;

        /// <summary>The room's number of the zombie, filled in by <see cref="HammerRules.NamedBlows"/>.</summary>
        public int NetId;
    }

    /// <summary>
    /// The rules behind the hammer. A blow is decided by the game: its click body asks the engine which
    /// zombies are around this machine's pointer and writes the damage onto each of them. The mod cannot
    /// ask that question again without answering it differently, so it only witnesses what the body did
    /// and hands the room the damage steps it saw.
    /// </summary>
    public static class HammerRules
    {
        /// <summary>
        /// Whether a damage step heard right now belongs to a blow this end should say. Only the end whose
        /// own click ran the tool body has a blow to report: an end replaying the room's order is running
        /// the same body the origin ran, and a second announcement of it would be the same blow twice.
        /// Outside a room nothing is witnessed at all, because a single player's lawn is not the mod's
        /// business and the list would only grow.
        /// </summary>
        public static bool Witnesses(bool roomDecides, bool blowing, bool applyingAReceivedCall)
        {
            return roomDecides && blowing && !applyingAReceivedCall;
        }

        /// <summary>
        /// Which damage steps of one blow are the room's business, in the order this lawn heard them and
        /// once per zombie. A zombie this end's ledger does not number is left out rather than guessed at:
        /// the room has no id for it, so the step it carries cannot be named anywhere else, and the count
        /// the caller prints says how many were dropped for that reason.
        /// </summary>
        public static HammerBlow[] NamedBlows(EntityLedger ledger, HammerBlow[] heard)
        {
            var named = new List<HammerBlow>();
            if (ledger == null || heard == null)
            {
                return new HammerBlow[0];
            }

            for (int i = 0; i < heard.Length; i++)
            {
                // The number is written onto every step the ledger can name, including one this blow
                // heard twice: the caller says which steps went nowhere, and a duplicate did not.
                int netId = heard[i].InstanceId == 0 ? 0 : NetIdOf(ledger, heard[i].InstanceId);
                heard[i].NetId = netId;

                if (netId != 0 && !Contains(named, netId))
                {
                    named.Add(heard[i]);
                }
            }

            return named.ToArray();
        }

        /// <summary>The room's number of a zombie this lawn touched; 0 for anything the ledger is not a zombie for.</summary>
        private static int NetIdOf(EntityLedger ledger, int instanceId)
        {
            NetEntity entry = ledger.ByInstanceId(instanceId);

            return entry == null || entry.Kind != NetKind.Zombie ? 0 : entry.NetId;
        }

        private static bool Contains(List<HammerBlow> named, int netId)
        {
            for (int i = 0; i < named.Count; i++)
            {
                if (named[i].NetId == netId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
