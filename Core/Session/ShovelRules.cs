namespace PvzMP.Core.Session
{
    /// <summary>
    /// The two rules the shovel needs, away from the game: which plant under the cursor the room has a
    /// record for, and whether that one may be dug. Everything else about digging is the game's own - the
    /// hit test, the animation, the hole - and the mod only ever repeats what the host agreed to.
    /// </summary>
    public static class ShovelRules
    {
        /// <summary>
        /// The first plant the ledger knows. The game decides what is under the pointer and can name two
        /// of them in one cell, so the mod does not choose between them; it only asks whether any of them
        /// is one the room grew. A plant the room did not mint - a level that starts with one, a wheat
        /// merge - stays the game's own business, and a peer digging it is a difference the log has to show.
        /// </summary>
        public static NetEntity TrackedPlant(EntityLedger ledger, int[] instanceIds)
        {
            if (ledger == null || instanceIds == null)
            {
                return null;
            }

            foreach (int instanceId in instanceIds)
            {
                NetEntity record = ledger.ByInstanceId(instanceId);
                if (record != null && record.Kind == NetKind.Plant)
                {
                    return record;
                }
            }

            return null;
        }

        /// <summary>
        /// The plant the room grew on the cell the tool points at. The game's hit test is the better
        /// answer and is asked first; this is the fallback for the frames where it names nothing, which
        /// is why a shovel held over a cell still reaches the host instead of digging on one lawn only.
        /// Where a cell holds more than one named plant the first record is the one named, and the host
        /// takes that one away on every lawn, so the room never disagrees about what disappeared.
        /// </summary>
        public static NetEntity TrackedPlantAt(EntityLedger ledger, int column, int row)
        {
            if (ledger == null || column < 0 || row < 0)
            {
                return null;
            }

            return ledger.PlantAt(column, row);
        }

        /// <summary>
        /// Why this plant may not be dug, in the words the refusal travels in, or null when it may. Whose
        /// card grew a plant is not one of the questions: any seat may take any of them back, which is what
        /// makes one lawn replannable by four people. The room only refuses a plant it never minted, because
        /// it cannot say where such a one went.
        /// </summary>
        public static string Refusal(NetEntity target)
        {
            if (target == null)
            {
                return "no plant of this room grows there";
            }

            if (target.Kind != NetKind.Plant)
            {
                return "#" + target.NetId + " is a " + target.Kind + ", the shovel takes plants";
            }

            return null;
        }
    }
}
