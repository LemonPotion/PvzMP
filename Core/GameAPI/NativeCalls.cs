using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using PvzMP.Core.Session;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The engine half of the one call frame: where a covered native method is announced, and where a
    /// received one is run. The applier is generic on purpose. It takes the key, finds the game's own
    /// method by the key's own grammar, resolves the target and the arguments from the reference tokens
    /// and the row's measured categories, and calls it. That is the whole reason a new synced feature is
    /// one row in the coverage table: no payload class, no gate, and nothing here to edit.
    ///
    /// What is not resolved is refused out loud, never guessed at. A scene path or an asset name is a
    /// reference the room can describe but this end cannot yet look up, so a frame naming one comes back
    /// as a refusal with the reason in the log - not as a stand-in object written by the mod, which is
    /// the one thing the project forbids: what the game can do, the game is asked to do.
    /// </summary>
    public static class NativeCalls
    {
        /// <summary>Switched off only when the reflection itself cannot reach the game's methods.</summary>
        public const string Capability = "native-call";

        /// <summary>The game's damage step on one named zombie: the door a hammer blow repeats.</summary>
        private const string DamageDoor = "Zombie.TakeDamage/5";

        private static MpSession _room;

        /// <summary>Resolved doors, and the reason the ones that do not resolve are refused.</summary>
        private static readonly Dictionary<string, MethodInfo> _doors =
            new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> _blocked =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Type> _types =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>
        /// The assembly the game's own types live in, taken from one facade type rather than a name
        /// string: the classes the coverage table names are all in it, and a build without them is a
        /// build this mod cannot run on at all.
        /// </summary>
        private static readonly Assembly GameAssembly = typeof(Il2Cpp.Board).Assembly;

        /// <summary>Told by the boot: which room to talk to, and that received calls now have someone to run them.</summary>
        public static void Attach(MpSession room)
        {
            _room = room;
            room.Calls.Applier = Apply;

            try
            {
                Log.Info("call", Audit(Arbiter.Default) + " " + ApiBase.FrameTag());
            }
            catch (Exception e)
            {
                // The applier is installed either way: a door the boot could not look up is answered
                // when its frame arrives, and the refusal then says what the lookup said here.
                Log.Error("call", "the covered doors could not be listed: " + e.GetType().Name + " "
                    + e.Message);
            }
        }

        /// <summary>
        /// A covered native method that this end's own game code just ran, offered to the room. The
        /// announcement question is the arbiter's, so a gate cannot put a row it does not have onto the
        /// wire, and a nested call inside a replay is quiet before it is even looked up. Which lane the
        /// frame takes is the row's answer too, so nothing here can move a door to the other promise.
        /// </summary>
        public static bool Announce(string key, string target, params object[] values)
        {
            MpSession room = _room;
            if (room == null || !CallInbox.ShouldAnnounce(room.RoomDecides))
            {
                return false;
            }

            Decision decision = Arbiter.Decide(Arbiter.Default, key, true, room.IsHost);
            if (!Arbiter.Travels(decision.Verdict))
            {
                Log.Warn("call", "not announcing " + key + ": " + decision.Reason);
                return false;
            }

            string args;
            try
            {
                args = CallArgs.Encode(decision.Fact.Cats, values);
            }
            catch (Exception e)
            {
                Log.Error("call", key + " cannot be written as a call: " + e.Message);
                return false;
            }

            bool latest = decision.Verdict == Verdict.Mirror;

            // A latest-wins frame takes no number: the ordered lane reads its own gaps out of the numbers
            // it is missing, and a frame that is not part of that stream would be read as one.
            MethodCall call = new MethodCall(key, target, args,
                latest ? 0 : room.Calls.NextSeq(), room.LocalPeerId);

            bool sent = room.IsHost ? room.BroadcastCall(call) : room.SendCallToHost(call);

            // The host's own announcement never passes through its inbox, so the value the next seat will
            // be told about is stored here rather than on the way in.
            if (latest && sent && room.IsHost)
            {
                room.Calls.Remember(call);
            }

            return sent;
        }

        /// <summary>
        /// The inbox's whole question: run this call on this lawn. Nothing here reports success that the
        /// game did not give: the reflection result is what the log line prints.
        /// </summary>
        public static CallOutcome Apply(MethodCall call)
        {
            CallOutcome outcome = ApiBase.Call("call " + call.Key, () => Run(call), Capability);

            // ApiBase answers with None when the guard blocked the call or the game threw. The inbox has
            // no outcome for "nothing happened", and it should not: a call that did not run either
            // deferred or failed, and saying so is the only way a silent room stays impossible.
            return outcome == CallOutcome.None ? CallOutcome.Failed : outcome;
        }

        private static CallOutcome Run(MethodCall call)
        {
            CoverageFact fact = Arbiter.Default.FindFact(call.Key);
            if (fact == null)
            {
                Log.Error("call", call.Key + " has no measured fact, so nothing here can build its "
                    + "arguments");
                return CallOutcome.Failed;
            }

            string reason;
            MethodInfo door = Door(call.Key, out reason);
            if (door == null)
            {
                Log.Error("call", reason);
                return CallOutcome.Failed;
            }

            object[] values = CallArgs.Decode(fact.Cats, call.Args);

            object target;
            CallOutcome outcome = ResolveTarget(door, call.Target, out target);
            if (outcome != CallOutcome.Applied)
            {
                return outcome;
            }

            ParameterInfo[] parameters = door.GetParameters();
            object[] argv = new object[values.Length];

            for (int i = 0; i < values.Length; i++)
            {
                CallOutcome ready = ToNative(values[i], fact.Cats[i], parameters[i].ParameterType,
                                             i, out argv[i]);
                if (ready != CallOutcome.Applied)
                {
                    return ready;
                }
            }

            door.Invoke(target, argv);

            // The host is the end whose zombie health every seat is told, so a blow that landed here has
            // to be visible in the very next push instead of in the next quarter second: the zombie it
            // killed is a despawn on every other lawn, not a body that stands until the timer. E62.
            if (call.Key == DamageDoor && _room != null && _room.IsHost)
            {
                ZombieSync.ForcePush();

                Log.Info("call", "the room's damage step ran on the host; the zombie state is pushed at"
                    + " once " + ApiBase.FrameTag());
            }

            return CallOutcome.Applied;
        }

        /// <summary>
        /// The method the key names: the class as it is spelled in the game assembly, the method as it is
        /// spelled there, and the declared parameter count the row was measured with. An overload with the
        /// same count is not resolved - two bodies for one key is a decision the table cannot make.
        /// </summary>
        private static MethodInfo Door(string key, out string reason)
        {
            MethodInfo door;
            if (_doors.TryGetValue(key, out door))
            {
                reason = null;
                return door;
            }

            if (_blocked.TryGetValue(key, out reason))
            {
                return null;
            }

            string classToken;
            string method;
            int arity;

            if (!Arbiter.TrySplitKey(key, out classToken, out method, out arity))
            {
                reason = key + " is not a class.method/arity key";
                _blocked[key] = reason;
                return null;
            }

            Type type = FindType(classToken);
            if (type == null)
            {
                reason = "the game assembly has no single class named " + classToken;
                _blocked[key] = reason;
                return null;
            }

            List<MethodInfo> matches = new List<MethodInfo>();
            foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                             | BindingFlags.Instance | BindingFlags.Static))
            {
                if (candidate.Name == method && candidate.GetParameters().Length == arity)
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count != 1)
            {
                reason = key + " is " + (matches.Count == 0 ? "not a method of " + type.Name
                                   : "an overload " + type.Name + " has more than one of")
                    + ", so no single body answers it";
                _blocked[key] = reason;
                return null;
            }

            door = matches[0];
            _doors[key] = door;

            return door;
        }

        private static Type FindType(string classToken)
        {
            Type found;
            if (_types.TryGetValue(classToken, out found))
            {
                return found;
            }

            Type match = null;
            int ambiguous = 0;
            Type[] candidates;

            try
            {
                candidates = GameAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // Types the loader could not bring in are null in this array, and a class the mod cannot
                // name is a door that will be refused with a reason rather than one that is not.
                candidates = e.Types;
            }

            foreach (Type candidate in candidates)
            {
                if (candidate == null || candidate.Name != classToken)
                {
                    continue;
                }

                match = candidate;
                ambiguous++;
            }

            found = ambiguous == 1 ? match : null;
            _types[classToken] = found;

            return found;
        }

        /// <summary>
        /// The object the game is asked to run the method on. A static needs nothing; a singleton is the
        /// game's own <c>Instance</c>; an entity is the room's id, read out of the ledger and then out of
        /// this lawn. An entity that is not here yet defers, because its spawn frame may still be on the
        /// way, and a call held for it is applied when it arrives.
        /// </summary>
        private static CallOutcome ResolveTarget(MethodInfo door, string token, out object target)
        {
            target = null;

            if (door.IsStatic)
            {
                return CallOutcome.Applied;
            }

            RefKind kind = CallArgs.KindOf(token);

            if (kind == RefKind.None)
            {
                return Singleton(door.DeclaringType, out target);
            }

            if (kind == RefKind.Entity)
            {
                return EntityTarget(CallArgs.NetIdOf(token), out target);
            }

            target = null;

            return Unresolved(kind, token);
        }

        private static CallOutcome Singleton(Type type, out object instance)
        {
            instance = null;

            const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Static;

            PropertyInfo property = type.GetProperty("Instance", StaticMembers);
            if (property != null)
            {
                instance = property.GetValue(null, null);
                return Present(instance)
                    ? CallOutcome.Applied
                    : Refuse(type.Name + ".Instance", "the game has no " + type.Name + " alive on this lawn");
            }

            FieldInfo field = type.GetField("Instance", StaticMembers);
            if (field != null)
            {
                instance = field.GetValue(null);
                return Present(instance)
                    ? CallOutcome.Applied
                    : Refuse(type.Name + ".Instance", "the game has no " + type.Name + " alive on this lawn");
            }

            return Refuse(type.Name + ".Instance", type.Name + " has no static Instance, so this end "
                + "cannot say which object to run the method on");
        }

        private static CallOutcome EntityTarget(int netId, out object target)
        {
            target = null;

            NetEntity entry = _room == null ? null : _room.Entities.Find(netId);
            if (entry == null || entry.InstanceId == 0)
            {
                return CallOutcome.Deferred;
            }

            object found;

            switch (entry.Kind)
            {
                case NetKind.Plant:
                    found = Entities.FindPlant(entry.InstanceId);
                    break;
                case NetKind.Zombie:
                    found = Entities.FindZombie(entry.InstanceId);
                    break;
                default:
                    return Refuse("#" + netId, "the room has no lookup for a " + entry.Kind
                        + " by its id yet");
            }

            if (found == null)
            {
                // The ledger knows the entity and this lawn does not: the spawn is still on the way, and
                // the retry queue is what it is for.
                return CallOutcome.Deferred;
            }

            target = found;

            return CallOutcome.Applied;
        }

        /// <summary>
        /// One decoded wire value into the parameter type the game declared, with the row's category as
        /// the guide. Only the categories with a shape this end can build are answered; the rest refuse.
        /// </summary>
        private static CallOutcome ToNative(object value, char cat, Type parameter, int index,
                                            out object native)
        {
            native = null;

            switch (cat)
            {
                case 'i':
                case 'e':
                case 'b':
                case 'f':
                    return Coerce(value, parameter, index, out native);
                case 'o':
                    return Reference((string) value, parameter, index, out native);
                default:
                    return Refuse("argument " + index + " [" + cat + "]", "this end cannot build a "
                        + parameter.Name + " from it yet");
            }
        }

        /// <summary>
        /// The wire shape of a number or a flag is JSON's; the game declared its own. A category that
        /// measured as an enum travels as its int, because that is what the native body reads.
        /// </summary>
        private static CallOutcome Coerce(object value, Type parameter, int index, out object native)
        {
            native = null;

            try
            {
                native = parameter.IsEnum
                    ? Enum.ToObject(parameter, Convert.ToInt64(value, CultureInfo.InvariantCulture))
                    : Convert.ChangeType(value, parameter, CultureInfo.InvariantCulture);

                return CallOutcome.Applied;
            }
            catch (Exception e)
            {
                return Refuse("argument " + index, "no " + parameter.Name + " can be built from it: "
                    + e.GetType().Name);
            }
        }

        private static CallOutcome Reference(string token, Type parameter, int index, out object native)
        {
            native = null;

            if (token == CallArgs.None)
            {
                return CallOutcome.Applied;
            }

            object target;
            CallOutcome outcome = ReferenceValue(CallArgs.KindOf(token), token, out target);
            if (outcome != CallOutcome.Applied)
            {
                return outcome;
            }

            if (target != null && !parameter.IsInstanceOfType(target))
            {
                object wrapped;
                if (!Rewrap(parameter, target, out wrapped))
                {
                    return Refuse(token, "argument " + index + " names a " + target.GetType().Name
                        + " where the game wants a " + parameter.Name);
                }

                target = wrapped;
            }

            native = target;

            return CallOutcome.Applied;
        }

        /// <summary>
        /// The game's own answer about a reference whose managed wrapper does not carry the interface.
        /// The generated proxies keep a class and its interfaces apart: `Hammer` derives from `InGameTool`
        /// and never from the proxy of `IDamageMaker`, so the managed check above refuses a maker that the
        /// game itself accepts. The native class is what counts, so the runtime is asked -
        /// `il2cpp_class_is_assignable_from` - and the same native object is wrapped as the declared type,
        /// because reflection only accepts the wrapper the parameter declares.
        /// Proof: ref/EVIDENCE.md E65.
        /// </summary>
        private static bool Rewrap(Type parameter, object target, out object wrapped)
        {
            wrapped = null;

            Il2CppObjectBase asObject = target as Il2CppObjectBase;
            if (asObject == null)
            {
                return false;
            }

            try
            {
                IntPtr wanted = Il2CppClassPointerStore.GetNativeClassPointer(parameter);

                if (wanted == IntPtr.Zero
                    || !IL2CPP.il2cpp_class_is_assignable_from(wanted, asObject.ObjectClass))
                {
                    return false;
                }

                wrapped = Activator.CreateInstance(parameter, asObject.Pointer);
                if (wrapped == null)
                {
                    return false;
                }

                Log.Info("call", target.GetType().Name + " is not a managed " + parameter.Name
                    + ", but the game's own class is; the same object is passed as " + parameter.Name
                    + " " + ApiBase.FrameTag());

                return true;
            }
            catch (Exception e)
            {
                Log.Warn("call", "the native class of " + target.GetType().Name + " could not be asked"
                    + " about " + parameter.Name + ": " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        private static CallOutcome ReferenceValue(RefKind kind, string token, out object value)
        {
            value = null;

            switch (kind)
            {
                case RefKind.Entity:
                    return EntityTarget(CallArgs.NetIdOf(token), out value);
                case RefKind.Singleton:
                    Type type = FindType(CallArgs.ValueOf(token));

                    return type == null
                        ? Refuse(token, "the game assembly has no single class named " + CallArgs.ValueOf(token))
                        : Singleton(type, out value);
                default:
                    return Unresolved(kind, token);
            }
        }

        /// <summary>
        /// A reference the wire can name and this end cannot yet find. Said out loud instead of filled
        /// in with an object the mod wrote itself, because a stand-in is a lawn the game did not build.
        /// </summary>
        private static CallOutcome Unresolved(RefKind kind, string token)
        {
            string what = kind == RefKind.ScenePath ? "scene path"
                : kind == RefKind.Asset ? "asset" : "reference";

            return Refuse(token, "a " + what + " has no lookup on this end yet");
        }

        /// <summary>
        /// Every door the room covers, looked up the way a received call looks it up - without calling
        /// it. This is the line that says whether the frame can reach the game at all, so a row that no
        /// longer matches the build is a refusal at boot instead of a lawn that quietly disagrees.
        /// </summary>
        private static string Audit(CoverageTable table)
        {
            int ready = 0;
            int blocked = 0;

            foreach (CoverageRule rule in table.RuleList)
            {
                if (rule.Verdict != Verdict.Replicate)
                {
                    continue;
                }

                string reason;
                if (Door(rule.Key, out reason) == null)
                {
                    blocked++;
                    Log.Warn("call", reason);
                    continue;
                }

                ready++;
            }

            return "native calls: " + ready + " covered door(s) resolve, " + blocked
                + " do not, " + table.RuleList.Count + " row(s) in the table";
        }

        private static CallOutcome Refuse(string what, string reason)
        {
            Log.Error("call", what + ": " + reason);
            return CallOutcome.Refused;
        }

        /// <summary>An il2cpp handle, whatever its declared proxy type, with the same liveness question.</summary>
        private static bool Present(object handle)
        {
            Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase asObject =
                handle as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;

            return asObject == null ? handle != null : ApiBase.Present(asObject);
        }
    }
}
