using Il2Cpp;

namespace PvzMP.Core.GameAPI
{
    /// <summary>
    /// The game's own menu stack, used instead of drawing a mod UI. Push(UIType) instantiates one of
    /// the game's own prefabs, so its cursor, its keyboard handling and its event system keep working
    /// - which is why the connection dialog can be operated at all on the mobile-derived port, where a
    /// self-drawn overlay would have to re-implement all three.
    ///
    /// ReNameMenu is the game's own rename/prompt screen: it carries a title, a TMP_InputField and a
    /// confirm UIButton, and the game's callers (save names, level names, garden pages) all wire that
    /// button themselves. So the mod asks by SetTitle, prefills by SetInputText, reads input.text and
    /// is told about the commit by the button's own clickEvent - no Harmony patch and no self-drawn
    /// text box anywhere.
    /// </summary>
    public static class Menu
    {
        public const string Capability = "mp-menu";
        public const UIType PromptMenu = UIType.ReNameMenu;

        /// <summary>The loader instance the game itself created; null before the first menu scene.</summary>
        public static UIResourcesLoader Loader()
        {
            return ApiBase.Query("GameAPP.UIManager", () => GameAPP.UIManager, Capability);
        }

        public static bool IsPromptRegistered()
        {
            return ApiBase.Query("UIPrefabs.ContainsKey(" + PromptMenu + ")", () =>
            {
                UIResourcesLoader loader = Loader();
                if (loader == null || !ApiBase.Present(loader.UIPrefabs))
                {
                    return false;
                }

                return loader.UIPrefabs.ContainsKey(PromptMenu);
            }, Capability);
        }

        public static int RegisteredPrefabs()
        {
            return ApiBase.Query("UIPrefabs.Count", () =>
            {
                UIResourcesLoader loader = Loader();
                return loader != null && ApiBase.Present(loader.UIPrefabs) ? loader.UIPrefabs.Count : -1;
            }, Capability);
        }

        /// <summary>Shows the game's text prompt over whatever screen is up. Null when it cannot be shown.</summary>
        public static BaseMenu PushPrompt()
        {
            return ApiBase.Call("Push(" + PromptMenu + ")", () =>
            {
                UIResourcesLoader loader = Loader();
                return loader == null ? null : loader.Push(PromptMenu, false);
            }, Capability);
        }

        /// <summary>The question shown above the box, in the game's own font and layout.</summary>
        public static void SetPromptTitle(BaseMenu prompt, string title)
        {
            ApiBase.TryCall("SetTitle", () =>
            {
                AsPrompt(prompt).SetTitle(title ?? string.Empty);
            }, Capability);
        }

        public static void SetPromptText(BaseMenu prompt, string text)
        {
            ApiBase.TryCall("SetInputText", () =>
            {
                AsPrompt(prompt).SetInputText(text ?? string.Empty);
            }, Capability);
        }

        /// <summary>What is in the box right now - the player may still be typing.</summary>
        public static string ReadPromptText(BaseMenu prompt)
        {
            return ApiBase.Query("input.text", () =>
            {
                string text = AsPrompt(prompt).input.text;
                return text == null ? string.Empty : text;
            }, Capability) ?? string.Empty;
        }

        /// <summary>
        /// The box is the game's name field, so it comes with the game's own character limit, and an ipv4
        /// address is longer than any name the prefab was made for. Read once per dialog so the log says
        /// what the prefab holds before the mod changes it.
        /// </summary>
        public static int ReadPromptTextLimit(BaseMenu prompt)
        {
            return ApiBase.Call("input.characterLimit", () => AsPrompt(prompt).input.characterLimit, Capability);
        }

        public static void SetPromptTextLimit(BaseMenu prompt, int limit)
        {
            ApiBase.TryCall("input.characterLimit = " + limit, () =>
            {
                AsPrompt(prompt).input.characterLimit = limit;
            }, Capability);
        }

        /// <summary>
        /// Runs the body every time the player presses the dialog's own confirm button. The game's
        /// callers attach closures to this same event, so the mod is using the intended mechanism;
        /// nothing is patched and the button keeps its native look, sound and interactable rules.
        /// </summary>
        public static void WatchConfirm(BaseMenu prompt, System.Action onConfirm)
        {
            ApiBase.TryCall("clickEvent.AddListener", () =>
            {
                UIButton button = AsPrompt(prompt).activeButton;
                if (!ApiBase.Present(button))
                {
                    throw new System.InvalidOperationException(PromptMenu + " has no confirm button");
                }

                button.clickEvent.AddListener(onConfirm);
            }, Capability);
        }

        /// <summary>
        /// True while this exact menu instance is the top of the game's stack. Pop closes the top menu
        /// whatever it is, so a mod that lost track of its own prompt would otherwise eat the screen
        /// underneath it - every close path checks here first.
        /// </summary>
        public static bool IsTop(BaseMenu prompt)
        {
            return ApiBase.Query("Peak() top is " + PromptMenu, () =>
            {
                UIResourcesLoader loader = Loader();
                if (prompt == null || loader == null)
                {
                    return false;
                }

                Il2CppSystem.Collections.Generic.List<BaseMenu> stack = loader.Peak();
                if (stack == null || stack.Count == 0)
                {
                    return false;
                }

                BaseMenu top = stack[stack.Count - 1];
                return ApiBase.Present(top) && top.GetInstanceID() == prompt.GetInstanceID();
            }, Capability);
        }

        /// <summary>The menu on top of the game's stack, or null when nothing is pushed.</summary>
        public static BaseMenu Top()
        {
            return ApiBase.Query("Peak top", () =>
            {
                UIResourcesLoader loader = Loader();
                if (loader == null)
                {
                    return null;
                }

                Il2CppSystem.Collections.Generic.List<BaseMenu> stack = loader.Peak();
                if (stack == null || stack.Count == 0)
                {
                    return null;
                }

                BaseMenu top = stack[stack.Count - 1];
                return ApiBase.Present(top) ? top : null;
            }, Capability);
        }

        /// <summary>
        /// Pops everything above one menu, which is what a nested pause submenu needs before the game's own
        /// resume runs: `BackToGame` pops exactly one screen, and popping the pause menu itself would make
        /// it take the in-game UI down instead. Zero stops immediately.
        /// </summary>
        public static int PopAbove(int stopInstanceId, int maxPops)
        {
            if (stopInstanceId == 0)
            {
                return 0;
            }

            int popped = 0;

            for (int i = 0; i < maxPops; i++)
            {
                BaseMenu top = Top();
                if (top == null || top.GetInstanceID() == stopInstanceId)
                {
                    break;
                }

                Pop();
                popped++;
            }

            return popped;
        }

        /// <summary>Closes the top menu the same way the game's own back button does.</summary>
        public static void Pop()
        {
            ApiBase.TryCall("Pop", () =>
            {
                UIResourcesLoader loader = Loader();
                if (loader != null)
                {
                    loader.Pop();
                }
            }, Capability);
        }

        /// <summary>
        /// The pushed menu, or a clear failure: reading the wrong prefab's field would put the player's
        /// typed address somewhere invisible instead of where the mod can see it.
        ///
        /// A C# downcast cannot be used here. Interop types the wrapper by Push's declared return type,
        /// so the managed object is always a BaseMenu and "prompt as ReNameMenu" is always null; the
        /// live self-test proved that. Cast checks the real native class and names it when it differs.
        /// </summary>
        private static ReNameMenu AsPrompt(BaseMenu prompt)
        {
            if (prompt == null)
            {
                throw new System.InvalidOperationException("no menu was pushed, cannot read it as " + PromptMenu);
            }

            return prompt.Cast<ReNameMenu>();
        }
    }
}
