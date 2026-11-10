namespace PvzMP.Core.Session
{
    /// <summary>
    /// Who may make a coin and who may take one, in a room where the sun pool is one number the host owns.
    ///
    /// Every coin in this game comes out of one native method, `CreateItem.SetCoin`, which the lawn's own
    /// timer walks about eight times a level for the sun that falls from the sky - and which two ends cannot
    /// be trusted to walk alike, because the first frame of the level is the moment each end started it. The
    /// room therefore lets one end mint and tells the other what appeared.
    ///
    /// A peer that is carrying out an order about a coin is that end doing it, so the rule has one shape for
    /// both doors: minting, and the death of a coin when it is collected.
    /// </summary>
    public static class SunRules
    {
        /// <summary>
        /// Whether this end may let the game create or destroy a coin of its own. False is a seated peer,
        /// whose coins are the host's to make and whose collection is the host's to answer; true is single
        /// player, the host, and any end obeying the host.
        /// </summary>
        public static bool OwnsItsCoins(bool roomDecides, bool isHost, bool applyingTheHostsOrder)
        {
            return !roomDecides || isHost || applyingTheHostsOrder;
        }

        /// <summary>
        /// How much the pool still has to gain after the game killed a coin for somebody else's click. The
        /// death is measured and not trusted: `CoinSun.Die` carries no body in the dump, and a room that
        /// guessed wrong either pays a sun twice or loses one.
        /// </summary>
        public static int StillMissing(int sunBefore, int sunAfter, int price)
        {
            if (price <= 0 || sunBefore < 0 || sunAfter < 0)
            {
                return 0;
            }

            int gained = sunAfter - sunBefore;

            return gained >= price ? 0 : price - gained;
        }
    }
}
