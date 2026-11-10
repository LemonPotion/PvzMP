using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace PvzMP.Core.GameAPI
{
    public enum MemberKind
    {
        Method,
        Field,
        Property
    }

    /// <summary>One native member the mod intends to use, as declared by the verified dump.</summary>
    public sealed class MemberSpec
    {
        public string Feature;
        public string Type;
        public string Member;
        public MemberKind Kind;
        public int Params;
        public string Returns;
        public bool Critical;

        public MemberSpec(string feature, string type, string member, MemberKind kind,
                          int paramCount, string returns, bool critical)
        {
            Feature = feature;
            Type = type;
            Member = member;
            Kind = kind;
            Params = paramCount;
            Returns = returns;
            Critical = critical;
        }

        public string Label => Type + "." + Member;
    }

    /// <summary>
    /// Resolves every member the mod plans to call against the assembly that is actually loaded
    /// in this build, then switches off the features whose members are absent. Members that are
    /// only reachable through reflection (private/internal) can only be verified here.
    /// What each resolved member actually does natively, and its proof, is in ref/EVIDENCE.md.
    /// </summary>
    public static class NativeProbe
    {
        private const string GameAssembly = "Assembly-CSharp";
        private const string GameNamespace = "Il2Cpp.";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private static readonly List<MemberSpec> Expected = BuildExpected();

        public static int MissingCount { get; private set; }
        public static int TotalCount { get { return Expected.Count; } }

        public static void Run()
        {
            if (Expected.Count == 0)
            {
                Log.Error("probe", "spec table is EMPTY: nothing was verified, do not read this run as a pass");
                MissingCount = -1;
                return;
            }

            Log.Info("probe", "verifying " + Expected.Count + " native members against the loaded "
                + GameAssembly + " ...");

            var brokenFeatures = new HashSet<string>(StringComparer.Ordinal);
            var coreBroken = new List<string>();
            int ok = 0;
            int kindNotes = 0;
            int accessNotes = 0;

            foreach (MemberSpec spec in Expected)
            {
                Result result = Check(spec);
                if (result.Problem == null)
                {
                    ok++;
                    if (result.KindNote != null)
                    {
                        kindNotes++;
                    }

                    if (result.AccessNote != null)
                    {
                        accessNotes++;
                    }

                    if (result.KindNote == null && result.AccessNote == null)
                    {
                        Log.Info("probe", "OK       " + Describe(spec));
                    }
                    else
                    {
                        Log.Warn("probe", "OK*      " + Describe(spec) + "  <<"
                            + string.Join("; ", Notes(result)) + ">>");
                    }

                    continue;
                }

                MissingCount++;
                Log.Warn("probe", result.Problem + " " + Describe(spec));
                if (brokenFeatures.Add(spec.Feature) && spec.Critical)
                {
                    coreBroken.Add(spec.Feature);
                }
            }

            foreach (string feature in brokenFeatures)
            {
                ApiBase.Disable(feature, "native member missing in this build");
            }

            Log.Info("probe", "resolved " + ok + "/" + Expected.Count
                + ", missing=" + MissingCount
                + ", kind-notes=" + kindNotes
                + ", non-public=" + accessNotes
                + ", features disabled=" + brokenFeatures.Count
                + ", api failures=" + ApiBase.FailureCount + " " + ApiBase.FrameTag());

            if (brokenFeatures.Count == 0)
            {
                Log.Info("probe", "DISABLED FEATURES: none");
                return;
            }

            Log.Error("probe", "DISABLED FEATURES: " + string.Join(", ", Sorted(brokenFeatures))
                + (coreBroken.Count > 0 ? "  [CORE MISSING: multiplayer cannot start]" : string.Empty));
        }

        private static IEnumerable<string> Notes(Result result)
        {
            if (result.KindNote != null)
            {
                yield return result.KindNote;
            }

            if (result.AccessNote != null)
            {
                yield return result.AccessNote;
            }
        }

        private static IEnumerable<string> Sorted(HashSet<string> values)
        {
            var list = new List<string>(values);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static string Describe(MemberSpec spec)
        {
            return spec.Label + " (" + spec.Kind.ToString().ToLowerInvariant()
                + (spec.Kind == MemberKind.Method ? ", " + spec.Params + " params, ->" + spec.Returns : ", " + spec.Returns)
                + ") [" + spec.Feature + (spec.Critical ? ", CORE" : string.Empty) + "]";
        }

        private struct Result
        {
            public string Problem;
            public string KindNote;
            public string AccessNote;

            public Result(string problem, string kindNote)
            {
                Problem = problem;
                KindNote = kindNote;
                AccessNote = null;
            }
        }

        /// <summary>
        /// A member that exists but is not public cannot be called from the mod's own code; it is
        /// still reachable through reflection on the proxy or a Harmony patch, so it is reported as
        /// a note instead of disabling the capability.
        /// </summary>
        private static string AccessNote(MethodBase member)
        {
            if (member == null || member.IsPublic)
            {
                return null;
            }

            if (member.IsPrivate)
            {
                return "proxy member is private";
            }

            if (member.IsAssembly)
            {
                return "proxy member is internal";
            }

            return member.IsFamily ? "proxy member is protected" : "proxy member is not public";
        }

        private static Result Check(MemberSpec spec)
        {
            Type type = Type.GetType(GameNamespace + spec.Type + ", " + GameAssembly, false);
            if (type == null)
            {
                return new Result("MISSING  type Il2Cpp." + spec.Type + " not in assembly", null);
            }

            if (spec.Kind == MemberKind.Method)
            {
                return CheckMethod(type, spec);
            }

            return CheckDataMember(type, spec);
        }

        /// <summary>
        /// Il2CppInterop proxies turn a native field into a C# property whose body calls
        /// il2cpp_field_get/set_value, and a native property into a C# property whose body calls
        /// the get_/set_ methods. Both shapes are one property to the caller, so the kind is read
        /// from the generated NativeFieldInfoPtr_&lt;name&gt; backing field and only reported as a note.
        /// </summary>
        private static Result CheckDataMember(Type type, MemberSpec spec)
        {
            PropertyInfo property = type.GetProperty(spec.Member, All);
            if (property != null)
            {
                if (!SameName(property.PropertyType.Name, spec.Returns))
                {
                    return new Result("MISMATCH property type is " + property.PropertyType.Name, null);
                }

                bool isField = HasNativeBackingField(type, spec.Member);
                Result found = new Result(null,
                    isField != (spec.Kind == MemberKind.Field)
                        ? "declared " + spec.Kind + ", native " + (isField ? "field" : "property")
                        : null);
                found.AccessNote = AccessNote(property.GetGetMethod(true));
                return found;
            }

            FieldInfo field = type.GetField(spec.Member, All);
            if (field != null)
            {
                if (!SameName(field.FieldType.Name, spec.Returns))
                {
                    return new Result("MISMATCH field type is " + field.FieldType.Name, null);
                }

                Result real = new Result(null, "real managed field, not an il2cpp proxy accessor");
                real.AccessNote = field.IsPublic ? null : "proxy member is private";
                return real;
            }

            MethodInfo accessor = type.GetMethod(spec.Member, All);
            if (accessor != null && accessor.GetParameters().Length == 0)
            {
                return SameName(accessor.ReturnType.Name, spec.Returns)
                    ? new Result(null, "call it as " + spec.Type + "." + spec.Member + "()")
                    : new Result("MISMATCH method returns " + accessor.ReturnType.Name, null);
            }

            return new Result("MISSING  " + spec.Kind.ToString().ToLowerInvariant(), null);
        }

        private static bool HasNativeBackingField(Type type, string name)
        {
            return type.GetField("NativeFieldInfoPtr_" + name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null;
        }

        private static Result CheckMethod(Type type, MemberSpec spec)
        {
            MethodInfo[] overloads = FindMethods(type, spec);
            if (overloads.Length == 0)
            {
                return type.GetMethod(spec.Member, All) == null
                    ? new Result("MISSING  method", null)
                    : new Result("MISMATCH  no overload with " + spec.Params + " params", null);
            }

            MethodInfo match = null;
            foreach (MethodInfo candidate in overloads)
            {
                if (candidate.IsPublic)
                {
                    match = candidate;
                    break;
                }
            }

            Result found = new Result(null, null);
            found.AccessNote = AccessNote(match ?? overloads[0]);
            return found;
        }

        private static MethodInfo[] FindMethods(Type type, MemberSpec spec)
        {
            var found = new List<MethodInfo>();
            foreach (MethodInfo method in type.GetMethods(All))
            {
                if (method.Name == spec.Member
                    && method.GetParameters().Length == spec.Params
                    && (spec.Returns == null || SameName(method.ReturnType.Name, spec.Returns)))
                {
                    found.Add(method);
                }
            }

            return found.ToArray();
        }

        /// <summary>Proxy names for game types may carry the il2cpp suffix for value types.</summary>
        private static bool SameName(string actual, string expected)
        {
            if (expected == null || actual == null)
            {
                return true;
            }

            return actual.StartsWith(expected, StringComparison.Ordinal);
        }

        private static List<MemberSpec> BuildExpected()
        {
            var s = new List<MemberSpec>();
            void M(string feature, string t, string m, int p, string r, bool crit = false)
                => s.Add(new MemberSpec(feature, t, m, MemberKind.Method, p, r, crit));
            void F(string feature, string t, string m, string r, bool crit = false)
                => s.Add(new MemberSpec(feature, t, m, MemberKind.Field, -1, r, crit));
            void P(string feature, string t, string m, string r, bool crit = false)
                => s.Add(new MemberSpec(feature, t, m, MemberKind.Property, -1, r, crit));

            // Board: instance, lists and sun pool.
            F("core-board", "Board", "Instance", "Board", true);
            F("core-board", "Board", "theSun", "Int32", true);
            F("core-board", "Board", "zombieArray", "List`1", true);
            F("core-board", "BoardEntity", "plantArray", "List`1", true);
            M("core-board", "Board", "SetSun", 1, "Void", true);
            M("core-board", "Board", "UseSun", 1, "Void", true);
            M("core-board", "Board", "GetSun", 2, "Void", true);
            M("core-board", "Board", "ClearTheBoard", 0, "Void", true);
            M("core-board", "Board", "OnPlantCreate", 1, "Void");
            M("core-board", "Board", "OnPlantDie", 2, "Void");
            F("sun", "Board", "maxSun", "Int32");
            F("sun", "Board", "theWave", "Int32");
            F("sun", "Board", "theMaxWave", "Int32");
            F("sun", "Board", "timeUntilNextWave", "Single");
            F("sun", "Board", "holdOnTime", "Single");
            P("sun", "Board", "CanStartNextWave", "Boolean");
            M("sun", "Board", "StartNextWave", 0, "Void");

            // Creators: the only supported way to make entities exist.
            F("core-plant", "CreatePlant", "Instance", "CreatePlant", true);
            M("core-plant", "CreatePlant", "SetPlant", 9, "Plant", true);
            M("core-plant", "CreatePlant", "CheckBox", 3, "Boolean", true);
            F("core-zombie", "CreateZombie", "Instance", "CreateZombie", true);
            M("core-zombie", "CreateZombie", "SetZombie", 5, "Zombie", true);
            F("core-bullet", "CreateBullet", "Instance", "CreateBullet");
            M("core-bullet", "CreateBullet", "SetBullet", 6, "Bullet");
            F("core-item", "CreateItem", "Instance", "CreateItem", true);
            M("core-item", "CreateItem", "SetCoin", 6, "GameObject", true);
            M("core-item", "CoinSun", "Die", 0, "Void", true);

            // Entity state readers.
            F("core-zombie", "Entity", "axis", "Transform");
            F("core-zombie", "Zombie", "theHealth", "Int64", true);
            F("core-zombie", "Zombie", "theMaxHealth", "Int64", true);
            F("core-zombie", "Zombie", "theZombieRow", "Int32", true);
            F("core-zombie", "Zombie", "theZombieType", "ZombieType", true);
            F("core-zombie", "Zombie", "theFirstArmorHealth", "Int32");
            F("core-zombie", "Zombie", "theSecondArmorHealth", "Int32");
            F("core-zombie", "Zombie", "theOriginSpeed", "Single");
            F("core-zombie", "Zombie", "freezeSpeed", "Single");
            F("core-zombie", "Zombie", "coldSpeed", "Single");
            F("core-zombie", "Zombie", "butterSpeed", "Single");
            F("core-zombie", "Zombie", "kelpSpeed", "Single");
            F("core-zombie", "Zombie", "garlicSpeed", "Single");
            F("core-zombie", "Zombie", "uniqueSpeed", "Single");
            F("core-zombie", "Zombie", "holeSpeed", "Single");
            F("core-zombie", "Zombie", "maLiangSpeed", "Single");
            M("core-zombie", "Zombie", "AdjustPosition", 1, "Void");
            P("core-zombie", "Zombie", "Alive", "Boolean", true);
            M("core-zombie", "Zombie", "Die", 1, "Void", true);
            F("core-plant", "Plant", "thePlantType", "PlantType", true);
            F("core-plant", "Plant", "thePlantColumn", "Int32", true);
            F("core-plant", "Plant", "thePlantRow", "Int32", true);
            F("core-plant", "Plant", "thePlantHealth", "Int32", true);
            F("core-plant", "Plant", "thePlantMaxHealth", "Int32", true);
            M("core-plant", "Plant", "Die", 1, "Void", true);

            // Waves are decided by the host only.
            M("wave-host", "BoardSpawner", "SummonZombies", 1, "Void");
            F("wave-host", "Board", "boardSpawner", "BoardSpawner");

            // Seed bank and battle start.
            F("seed-bank", "InGameUI", "Instance", "InGameUI");
            F("seed-bank", "InGameUI", "cards", "HashSet`1");
            M("seed-bank", "InGameUI", "AddCardToBank", 2, "Boolean");
            M("seed-bank", "InGameUI", "RemoveCardFromBank", 2, "Boolean");
            M("seed-bank", "InGameUI", "OnStartBattleButtonClick", 0, "Void");
            P("seed-bank", "InGameUI", "CardSlotManager", "CardSlotManager");
            M("seed-bank", "CardSlotManager", "SetMaxCardCount", 1, "Void");
            F("seed-bank", "InitBoard", "Instance", "InitBoard");
            F("seed-bank", "InitBoard", "ready", "Boolean");
            M("seed-bank", "InitBoard", "CreateCard", 3, "CardUI");
            M("seed-bank", "InitBoard", "ShowSeedBank", 0, "UniTask");
            F("seed-bank", "CardUI", "thePlantType", "PlantType");
            F("seed-bank", "CardUI", "theSeedCost", "Int32");

            // What a card carries besides a picture: the price the room reads instead of invents, and the
            // cooldown whose value says whether the game's own card path was the one that planted.
            P("seed-bank", "InGameUI", "Cards", "List`1");
            F("seed-bank", "CardUI", "onCardBank", "Boolean");
            F("seed-bank", "CardUI", "CD", "Single");
            F("seed-bank", "CardUI", "fullCD", "Single");

            // Level flow: the game's own navigation, all public static.
            M("level-flow", "UIMgr", "EnterGame", 4, "Void");
            M("level-flow", "UIMgr", "GetSceneType", 2, "SceneType");
            M("level-flow", "UIMgr", "BackToGame", 0, "Void");
            M("level-flow", "UIMgr", "EnterMainMenu", 0, "Void");
            M("level-flow", "UIMgr", "EnterPauseMenu", 0, "Void");
            M("level-flow", "UIMgr", "EnterLoseMenu", 1, "Void");

            // Application state and native services.
            F("app-state", "GameAPP", "Instance", "GameAPP", true);
            F("app-state", "GameAPP", "theGameStatus", "GameStatus", true);
            F("app-state", "GameAPP", "playerName", "String");
            F("app-state", "GameAPP", "canvasUp", "Transform");
            F("level-settings", "GameAPP", "config", "GameConfig");
            F("level-settings", "GameConfig", "difficulty", "Int32");
            F("level-settings", "GameConfig", "gameSpeed", "Single");
            F("app-state", "GameAPP", "theBoardLevel", "Int32");
            F("app-state", "GameAPP", "theBoardType", "LevelType");
            F("app-state", "GameAPP", "resourcesManager", "ResourcesManager");
            M("app-state", "GameAPP", "PlaySound", 3, "Void");

            // The connection dialog is the game's own prompt menu, not a mod-drawn overlay.
            F("mp-menu", "GameAPP", "UIManager", "UIResourcesLoader", true);
            F("mp-menu", "UIResourcesLoader", "UIPrefabs", "Dictionary`2");
            M("mp-menu", "UIResourcesLoader", "Push", 2, "BaseMenu");
            M("mp-menu", "UIResourcesLoader", "Pop", 0, "Void");
            M("mp-menu", "UIResourcesLoader", "Peak", 0, "List`1");
            F("mp-menu", "UIType", "ReNameMenu", "UIType");
            M("mp-menu", "ReNameMenu", "SetTitle", 1, "Void");
            M("mp-menu", "ReNameMenu", "SetInputText", 1, "Void");
            F("mp-menu", "ReNameMenu", "input", "TMP_InputField");
            F("mp-menu", "ReNameMenu", "activeButton", "UIButton");
            F("mp-menu", "UIButton", "clickEvent", "UnityEvent");

            // Placement preview / ghost sources, needed without spawning a real entity.
            F("preview", "ResourcesManager", "plantPreviews", "Dictionary`2");
            M("preview", "CreatePlant", "CreatePlantPreview", 4, "GameObject");
            M("preview", "CreateZombie", "CreateZombiePreview", 4, "GameObject");
            M("preview", "MixData", "TryGetMix", 4, "Boolean", true);

            // Local input: intent capture and hovered cell, read natively.
            F("input", "Mouse", "Instance", "Mouse");
            F("input", "Mouse", "theMouseRow", "Int32");
            F("input", "Mouse", "theMouseColumn", "Int32");
            F("input", "Mouse", "theCardOnMouse", "CardUI");
            M("input", "Mouse", "GetBoxXFromColumn", 1, "Single");
            M("input", "Mouse", "GetBoxYFromRow", 1, "Single");
            M("input", "Mouse", "GetPlantsOnMouse", 0, "List`1");
            M("input-priv", "Mouse", "SetPlantFromCard", 3, "Plant");
            M("input-priv", "Mouse", "TryToSetPlantByCard", 0, "Void");

            // The game's own merge paths, watched rather than called: which one a click walks is what the
            // live run has to answer before the room can copy a merge at all.
            M("input-priv", "Mouse", "ReinforcePlant", 0, "Void");
            M("input-priv", "Mouse", "TurnPlantsToNewPlants", 3, null);
            M("input-priv", "Mouse", "LeftClickWithSomeThing", 0, "Void");
            F("shovel", "Shovel", "Instance", "Shovel");
            F("shovel", "InGameTool", "isPickUp", "Boolean");
            M("shovel", "Shovel", "OnClick", 1, "Boolean");
            M("shovel", "Shovel", "Use", 1, "Void");

            return s;
        }
    }
}
