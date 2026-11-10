namespace PvzMP.Core.Session
{
    /// <summary>
    /// The rules the effect relay needs away from the game: which seat plays what it was told about, and
    /// which of the game's sounds the room carries. Native proof: ref/EVIDENCE.md E50.
    /// </summary>
    public static class EffectRules
    {
        /// <summary>Kind 0: a particle made by `ParticleManager.SetParticle`.</summary>
        public const int ManagerParticle = 0;

        /// <summary>Kind 1: a particle made by `CreateParticle.SetParticle`.</summary>
        public const int CreatedParticle = 1;

        /// <summary>Kind 2: a sound made by `GameAPP.PlaySound`.</summary>
        public const int Sound = 2;

        /// <summary>
        /// Whether this lawn plays an effect it was told of. The seat whose own lawn made it played it
        /// through the game's own door already, so it plays nothing twice; every other seat - the peers that
        /// hear the host and the host that carries a peer's effect on - plays it once.
        /// </summary>
        public static bool PlaysHere(int originPeer, int localPeer)
        {
            return originPeer != localPeer;
        }

        /// <summary>
        /// Whether a sound may travel. The door is the game's own audio call, made from hundreds of places
        /// that are not events at all - a card that idles, a menu that opens - so only a sound made while a
        /// known event is being played is the room's business.
        /// </summary>
        public static bool CarriesSound(int eventDepth)
        {
            return eventDepth > 0;
        }
    }
}
