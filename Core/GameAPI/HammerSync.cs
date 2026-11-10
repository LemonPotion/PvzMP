using System.Collections.Generic;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The hammer of a room. The click is not the thing that travels: the body it runs finds its zombies
    /// with an engine query around this machine's pointer, so a seat cannot tell another seat what it
    /// clicked at, only what its own game decided to hit. The blow that kills is the animation event
    /// `AnimCrush`, not `Use`; the mod watches the game's own damage step while that body runs and
    /// repeats every step it heard through the same door the game used, `Zombie.TakeDamage`, with the
    /// numbers the game computed and this lawn's own hammer as the maker. The door the room used before,
    /// `Hammer.EffectZombie`, is not a blow at all: its body draws bounds, an explosion and a freeze and
    /// contains no damage step, which is why a peer's kill left the host's zombie standing (E60).
    /// Proof: ref/EVIDENCE.md E54, E57, E60.
    ///
    /// The lawn that clicked is never told twice. Its own body already hit those zombies, and the frame it
    /// sends comes back without it: a peer asks the host, the host runs the damage on its own lawn and then
    /// passes it to every seat but the one that asked. Because the host is the end whose zombie health
    /// every other lawn is later told, a blow that lands there lands everywhere.
    ///
    /// A blow that cannot be named is dropped, not guessed at. The room numbers a zombie by its own id, so
    /// a damage step the ledger does not know - one the host has not announced, or one that died between
    /// the query and this line - has no id to carry and is left out of the order. The count printed says so.
    /// </summary>
    public static class HammerSync
    {
        public const string Capability = "mp-hammer";

        /// <summary>The key the coverage table spells: the game's damage step on one named zombie.</summary>
        private const string Door = "Zombie.TakeDamage/5";

        private static MpSession _room;

        /// <summary>True while this end's own click runs the game's hammer body, which is when a damage step is news.</summary>
        private static bool _blowing;

        /// <summary>The damage steps that body took, in the order it took them, as this machine's ids.</summary>
        private static readonly List<HammerBlow> _touched = new List<HammerBlow>();

        /// <summary>The same zombies, so a zombie heard on two doors of one blow is counted once.</summary>
        private static readonly HashSet<int> _seen = new HashSet<int>();

        /// <summary>Told by the boot: which room a blow has to be said to, and that the tool is now witnessed.</summary>
        public static void Attach(MpSession room)
        {
            _room = room;

            HammerGate.Install();

            Log.Info("mp", "the hammer is witnessed on this lawn and its blow is repeated on every other, "
                + "the host's first; the room is told " + Door + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked before the game's own hammer body runs. Whether anything is witnessed is the room's
        /// question and not this end's: outside a room the body changes one lawn and has nothing to say.
        /// The door that opened the window is named, because `Use` and `AnimCrush` are a third of a second
        /// apart and only one of them carries the damage (E57, E60).
        /// </summary>
        public static void BeginBlow(string door)
        {
            _touched.Clear();
            _seen.Clear();
            _blowing = HammerRules.Witnesses(TheRoomDecides(), true, CallInbox.IsApplying);

            if (_blowing)
            {
                Log.Info("mp", "the hammer's window is open on " + door + " " + ApiBase.FrameTag());
            }
        }

        /// <summary>
        /// Asked by the witness on the game's own damage step: this zombie was touched by the blow, with
        /// the damage and the classification the game's body computed. The maker is kept by the class the
        /// game actually passed - the tool, `Hammer` - rather than by the interface the parameter declares,
        /// because the other lawns can only resolve a class with its own `Instance` (E57, E60).
        /// </summary>
        public static void NoteBlow(Il2Cpp.Zombie zombie, int damage, Il2Cpp.IDamageMaker maker,
                                    Il2Cpp.DamageType kind, Il2Cpp.PlantType reportType, bool fix)
        {
            if (!_blowing || !ApiBase.Present(zombie))
            {
                return;
            }

            int instanceId = zombie.GetInstanceID();
            if (!_seen.Add(instanceId))
            {
                return;
            }

            // The maker has to be a class the other lawns can look up, and the parameter in front of this
            // body is the interface, not that class: the interop object's own type name is `IDamageMaker`,
            // which no lawn has a `Instance` for. What the native dispatch really passes is the tool, and
            // the tool's class is Hammer (E57: `r9` is the tool at AnimCrush +0x30F). The interface name
            // is printed too, because it is the reason this line cannot use the object's own type; the
            // live refusal the old token caused is measured in E62.
            string proxyName = ApiBase.Present(maker) ? maker.GetType().Name : null;
            string makerName = ApiBase.Present(maker) ? "Hammer" : null;

            _touched.Add(new HammerBlow
            {
                InstanceId = instanceId,
                Damage = damage,
                Maker = makerName,
                DamageType = (int)kind,
                ReportType = (int)reportType,
                Fix = fix
            });

            // The numbers are the game's own, so they are written down as it passed them: a blow that is
            // repeated with a different amount is the failure this line is here to rule out.
            Log.Info("mp", "the crush touched " + Entities.Describe(zombie) + " for " + damage
                + " from " + (makerName ?? "nothing")
                + (proxyName == null || proxyName == makerName ? "" : " (the body hands it over as "
                    + proxyName + ")")
                + ", kind " + (int)kind + ", reports " + (int)reportType
                + (fix ? ", keeps it at one hit point" : "") + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked after that body returned. Every damage step it took is now one call the room is told
        /// about, in the order this lawn heard them and once per zombie.
        /// </summary>
        public static void EndBlow()
        {
            if (!_blowing)
            {
                return;
            }

            _blowing = false;

            if (_touched.Count == 0)
            {
                Log.Info("mp", "this lawn's hammer fell and touched nothing " + ApiBase.FrameTag());
                return;
            }

            HammerBlow[] heard = _touched.ToArray();
            _touched.Clear();
            _seen.Clear();

            HammerBlow[] named = HammerRules.NamedBlows(_room.Entities, heard);

            for (int i = 0; i < heard.Length; i++)
            {
                if (heard[i].NetId == 0)
                {
                    // NamedBlows fills NetId only on the steps the room numbers, so a zero here is the
                    // step that went nowhere: the host has not announced this zombie yet, or it died
                    // between the query and this line.
                    Log.Info("mp", "the step on this lawn's zombie #" + heard[i].InstanceId + " ("
                        + heard[i].Damage + ") has no room number yet, so it is left out of the order "
                        + ApiBase.FrameTag());
                }
            }

            int said = 0;
            for (int i = 0; i < named.Length; i++)
            {
                string maker = named[i].Maker == null ? CallArgs.None : CallArgs.Singleton(named[i].Maker);

                if (NativeCalls.Announce(Door, CallArgs.Entity(named[i].NetId), named[i].Damage, maker,
                        named[i].DamageType, named[i].ReportType, named[i].Fix))
                {
                    said++;
                }
            }

            Log.Info("mp", "this lawn's hammer touched " + heard.Length + " zombie(s) and the room is told "
                + said + " damage step(s) of " + named.Length + " named, " + (_room.IsHost
                    ? "every other seat repeats them"
                    : "the host takes them onto its own lawn first") + " " + ApiBase.FrameTag());
        }

        /// <summary>
        /// Asked by the three watched doors of the tool. The dump cannot say which of them a live click
        /// walks, and a Unity animation event is exactly the caller a static scan does not see, so the
        /// answer is written to the log and nowhere else.
        /// </summary>
        public static void DescribeDoor(string what)
        {
            if (!TheRoomDecides())
            {
                return;
            }

            Log.Info("mp", "the hammer door " + what + " while the room decides " + ApiBase.FrameTag());
        }

        /// <summary>A blow heard on the lawn that is gone cannot be answered on the next one.</summary>
        public static void ForgetTheLawn()
        {
            if (_blowing || _touched.Count > 0)
            {
                Log.Info("mp", "the lawn went away with the hammer's window " + (_blowing ? "open" : "closed")
                    + " and " + _touched.Count + " unwitnessed step(s), so nothing is said about them "
                    + ApiBase.FrameTag());
            }

            _blowing = false;
            _touched.Clear();
        }

        private static bool TheRoomDecides()
        {
            return _room != null && _room.RoomDecides && !ApiBase.IsBroken(Capability);
        }
    }
}
