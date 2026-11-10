using System;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The pause menu's two sliders: the level difficulty and the game speed multiplier. They are the
    /// plain fields `DifficultyMgr.Update` and `GameSpeedMgr.Update` write on `GameAPP.config`
    /// (ref/EVIDENCE.md E20), and the game's own update applies the speed to its clock. The host reads
    /// them and the room copies them, so both screens run the level at the same difficulty and pace.
    /// </summary>
    public static class LevelSettings
    {
        public static bool Read(out int difficulty, out float gameSpeed)
        {
            int readDifficulty = -1;
            float readSpeed = -1f;

            bool found = ApiBase.Query("ReadLevelSettings", () =>
            {
                Il2Cpp.GameConfig config = Il2Cpp.GameAPP.config;
                if (!ApiBase.Present(config))
                {
                    return false;
                }

                readDifficulty = config.difficulty;
                readSpeed = config.gameSpeed;
                return true;
            }, "level-settings");

            difficulty = readDifficulty;
            gameSpeed = readSpeed;
            return found;
        }

        /// <summary>
        /// Stores the host's numbers in this end's own config object. The sliders' `Update` reads the same
        /// two fields, so this end's menu shows the host's values on its next frame. The caller is the
        /// session tick on the game's main thread.
        /// </summary>
        public static bool Apply(int difficulty, float gameSpeed)
        {
            if (!ApiBase.OnMainThread)
            {
                Log.Error("api", "UpdateLevelSettings was called off the game's main thread");
                return false;
            }

            Il2Cpp.GameConfig config = Il2Cpp.GameAPP.config;
            if (!ApiBase.Present(config))
            {
                return false;
            }

            if (config.difficulty == difficulty && config.gameSpeed == gameSpeed)
            {
                return true;
            }

            config.difficulty = difficulty;
            config.gameSpeed = gameSpeed;
            return true;
        }

        /// <summary>
        /// Moves this end's own two sliders to the host's values. The menu's `Update` writes its slider
        /// into `GameAPP.config` every frame while it is open, so a config write alone would be undone;
        /// the slider is the real source there. The game speed slider is an index into the game's own
        /// `GameSpeedMgr.Gears` list, so the host's speed is matched to the nearest gear.
        /// </summary>
        public static void SyncSliders(int difficulty, float gameSpeed)
        {
            if (!ApiBase.OnMainThread)
            {
                return;
            }

            if (UnityEngine.Time.timeScale != 0f)
            {
                return;
            }

            try
            {
                Il2Cpp.DifficultyMgr difficultyMgr = UnityEngine.Object.FindObjectOfType<Il2Cpp.DifficultyMgr>();
                if (ApiBase.Present(difficultyMgr))
                {
                    UnityEngine.UI.Slider slider = difficultyMgr.GetComponent<UnityEngine.UI.Slider>();
                    if (ApiBase.Present(slider) && (int)slider.value != difficulty)
                    {
                        slider.value = difficulty;
                    }
                }

                Il2Cpp.GameSpeedMgr speedMgr = UnityEngine.Object.FindObjectOfType<Il2Cpp.GameSpeedMgr>();
                if (ApiBase.Present(speedMgr))
                {
                    int gear = GearIndex(gameSpeed);
                    UnityEngine.UI.Slider slider = speedMgr.GetComponent<UnityEngine.UI.Slider>();

                    if (gear >= 0 && ApiBase.Present(slider) && (int)slider.value != gear)
                    {
                        slider.value = gear;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("api", "the level sliders could not be moved: " + e.GetType().Name + ": "
                    + e.Message);
            }
        }

        private static int GearIndex(float gameSpeed)
        {
            Il2CppSystem.Collections.Generic.List<float> gears = Il2Cpp.GameSpeedMgr.Gears;
            if (!ApiBase.Present(gears) || gears.Count == 0)
            {
                return -1;
            }

            int best = -1;
            float bestDelta = float.MaxValue;

            for (int i = 0; i < gears.Count; i++)
            {
                float delta = Math.Abs(gears[i] - gameSpeed);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }

            return best;
        }
    }
}
