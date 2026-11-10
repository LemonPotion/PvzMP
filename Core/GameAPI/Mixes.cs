namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The game's own fusion table, asked as a question. `MixData.TryGetMix` is a pure lookup of the
    /// recipe dictionary (ref/EVIDENCE.md E20), so it is safe inside a silent Query and tells the mod
    /// both whether two plants fuse and into which type. The mod never performs the fusion itself:
    /// `CreatePlant.CheckMix` still does that when the host's click runs.
    /// </summary>
    public static class Mixes
    {
        public const int NoRecipe = -1;

        /// <summary>
        /// The type two plants fuse into, or NoRecipe. `order` is left at the game's default, which is
        /// the same call the game's own fusion makes; the method itself tries the recipe both ways.
        /// </summary>
        public static int Result(int firstType, int secondType)
        {
            int result = NoRecipe;

            bool found = ApiBase.Query("MixData.TryGetMix(" + firstType + "," + secondType + ")", () =>
            {
                Il2Cpp.PlantType mixed;

                if (!Il2Cpp.MixData.TryGetMix((Il2Cpp.PlantType)firstType,
                    (Il2Cpp.PlantType)secondType, out mixed))
                {
                    return false;
                }

                result = (int)mixed;
                return true;
            }, "preview");

            return found ? result : NoRecipe;
        }
    }
}
