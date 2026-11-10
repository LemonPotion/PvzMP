using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// Runs the read-only half of the facade once, right after boot, before any session exists.
    /// Reflection in NativeProbe only proves a member is there; this proves the mod can actually
    /// execute into the game's code from the main thread and get a sane value back.
    /// Nothing here may create, change or destroy anything: it has to be safe in single player.
    /// </summary>
    public static class NativeSmoke
    {
        public static void Run()
        {
            Log.Info("smoke", "read-only native smoke start");

            Log.Info("smoke", "App.DescribeStatus -> " + App.DescribeStatus()
                + " (the measured run showed this field does not track battles: the main menu reads"
                + " 0/InGame, so only the lawn is used as a battle test)");

            Log.Info("smoke", "App.ReadPlayerName -> '" + App.ReadPlayerName() + "'");
            Log.Info("smoke", "SunPool.ReadSun -> " + SunPool.ReadSun()
                + " (expected -1: no lawn yet)");
            Log.Info("smoke", "Waves.ReadWave -> " + Waves.ReadWave()
                + "/" + Waves.ReadMaxWave() + " nextIn=" + Waves.ReadTimeUntilNextWave()
                + " canStart=" + Waves.CanStartNextWave());
            Log.Info("smoke", "Cursor -> cell=" + Cursor.ReadColumn() + "," + Cursor.ReadRow()
                + " heldCard=" + Cursor.ReadHeldCardPlantType());

            PlantState[] plants = Plants.ReadAll();
            ZombieState[] zombies = Zombies.ReadAll();
            Log.Info("smoke", "Plants.ReadAll -> " + Count(plants)
                + ", Zombies.ReadAll -> " + Count(zombies)
                + ", BoardCollections -> plants=" + Count(BoardCollections.PlantInstanceIds())
                + " zombies=" + Count(BoardCollections.ZombieInstanceIds()));

            Log.Info("smoke", "Menu.Loader present=" + ApiBase.Present(Menu.Loader())
                + " registeredPrefabs=" + Menu.RegisteredPrefabs()
                + " promptRegistered=" + Menu.IsPromptRegistered()
                + " (false at boot is not yet a verdict: the menu scene may fill UIManager later)");

            Log.Info("smoke", "native calls that failed=" + ApiBase.FailureCount
                + " (any exception here would be a bug: every path is a pure read)");
            Log.Info("smoke", "read-only native smoke done");
        }

        private static int Count(Array values)
        {
            return values == null ? -1 : values.Length;
        }
    }
}
