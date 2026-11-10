using MelonLoader;
using PvzMP.Core;
using PvzMP.Core.GameAPI;

[assembly: MelonInfo(typeof(PvzMP.ModMain), "PvzMP", "0.1", "LemonPotion")]
[assembly: MelonGame("LanPiaoPiao", "PlantsVsZombiesRH")]

namespace PvzMP
{
    public class ModMain : MelonMod
    {
        public ModMain()
        {
            MelonEvents.OnApplicationStart.Subscribe(Bootstrap);
            MelonEvents.OnUpdate.Subscribe(MainThreadPump.Run);
            MelonEvents.OnUpdate.Subscribe(MenuWatch.Tick);
            MelonEvents.OnUpdate.Subscribe(CoopMenu.Tick);
            MelonEvents.OnUpdate.Subscribe(ConnectionEntry.Tick);
            MelonEvents.OnUpdate.Subscribe(LobbyHud.Tick);
            MelonEvents.OnUpdate.Subscribe(ReadyLabel.Tick);
            MelonEvents.OnUpdate.Subscribe(LevelSync.Tick);
            MelonEvents.OnUpdate.Subscribe(ZombieSync.Tick);
            MelonEvents.OnLateUpdate.Subscribe(ZombieSync.LateTick);
            MelonEvents.OnUpdate.Subscribe(PlantSync.Tick);
            MelonEvents.OnUpdate.Subscribe(GraveSync.Tick);
            MelonEvents.OnUpdate.Subscribe(PreviewSync.Tick);
            MelonEvents.OnUpdate.Subscribe(PlantSelect.Tick);
            MelonEvents.OnUpdate.Subscribe(SlowSync.Tick);
        }

        private static void Bootstrap()
        {
            Log.Info("boot", "PvzMP 0.1 starting");
            ApiBase.AttachMainThread();
            NativeProbe.Run();
            NativeSmoke.Run();
            WorldSync.Attach(ConnectionEntry.Room);
            NativeCalls.Attach(ConnectionEntry.Room);
            LevelSync.Attach(ConnectionEntry.Room);
            PauseSync.Attach(ConnectionEntry.Room);
            ZombieSync.Attach(ConnectionEntry.Room);
            PlantSync.Attach(ConnectionEntry.Room);
            GraveSync.Attach(ConnectionEntry.Room);
            FertilizeSync.Attach(ConnectionEntry.Room);
            HammerSync.Attach(ConnectionEntry.Room);
            TrophySync.Attach(ConnectionEntry.Room);
            PreviewSync.Attach(ConnectionEntry.Room);
            EndGate.Attach(ConnectionEntry.Room);
            ActionGate.Install();
            PlantSelect.Attach(ConnectionEntry.Room);
            RewardSync.Attach(ConnectionEntry.Room);
            SlowSync.Attach(ConnectionEntry.Room);
            EffectSync.Attach(ConnectionEntry.Room);
            SoundSync.Attach(ConnectionEntry.Room);
            Log.Info("boot", "ready; no session exists, single player is untouched");
        }
    }
}
