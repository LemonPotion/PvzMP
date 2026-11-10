using System.Collections.Generic;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The sweep key. It takes down the objects this lawn is holding that the room has no word about - a
    /// victory cup from a level that is over, a coin the room never minted - so a player whose screen kept
    /// one of them is not left clicking at an object every gate is right to refuse.
    ///
    /// It is a room's tool and it is local: nothing is announced, because what the room said stays the
    /// room's and what it never said belongs to nobody. Outside a room the key does nothing at all, so a
    /// single player's lawn is untouched.
    /// </summary>
    public static class LawnSweep
    {
        public static void Sweep(MpSession room)
        {
            if (room == null)
            {
                return;
            }

            bool decides = room.RoomDecides;

            if (!decides)
            {
                Log.Info("mp", "nothing swept: the sweep key is a room's tool and this end is "
                    + room.Phase);

                return;
            }

            int cups = TrophySync.SweepCups();
            int coins = SweepCoins(room, decides);

            Log.Info("mp", "the sweep key took " + cups + " cup(s) and " + coins
                + " coin(s) off this lawn, the room was told nothing " + ApiBase.FrameTag());
        }

        private static int SweepCoins(MpSession room, bool decides)
        {
            List<int> held = Coins.Held();
            int taken = 0;

            for (int i = 0; i < held.Count; i++)
            {
                // A coin the room minted is the room's object and stays: this end may still click it, and
                // the credit that click asks for is answered by the host.
                if (!SweepRules.MayTakeDown(decides, room.Entities.ByInstanceId(held[i]) != null))
                {
                    continue;
                }

                if (Coins.Despawn(held[i]))
                {
                    taken++;
                }
            }

            return taken;
        }
    }
}
