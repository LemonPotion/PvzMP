using System.Collections.Generic;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// A nickname is the only piece of player text that ends up in everybody's roster, so its rules live
    /// here rather than at each call site: the same check decides whether a typed name is usable and
    /// whether a de-duplicated one still is.
    ///
    /// The native profile name is 'Player' for everyone in this build, so colliding nicks are the normal
    /// case, not an edge case, and the host has to renumber them from the first version.
    /// </summary>
    public static class NickRules
    {
        public const int MinLength = 3;
        public const int MaxLength = 16;

        /// <summary>Four seats means #2..#4 in practice; the limit only says when to stop trying.</summary>
        public const int MaxSuffixNumber = 9;

        public const string Fallback = "Player";

        public static bool IsAllowed(string nick)
        {
            if (nick == null || nick.Length < MinLength || nick.Length > MaxLength)
            {
                return false;
            }

            for (int i = 0; i < nick.Length; i++)
            {
                if (!IsAllowedChar(nick[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>What the player typed when it works, the profile name when it does not, and never an empty nick.</summary>
        public static string Resolve(string typed, string profileName)
        {
            string cleaned = Clean(typed);
            if (IsAllowed(cleaned))
            {
                return cleaned;
            }

            cleaned = Clean(profileName);
            return IsAllowed(cleaned) ? cleaned : Fallback;
        }

        /// <summary>
        /// Spaces are dropped instead of rejecting the whole name: nobody means anything rude by
        /// "Big Bob", and the alternative is a dialog that silently refuses to accept it.
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            char[] kept = new char[text.Length];
            int count = 0;

            for (int i = 0; i < text.Length && count < MaxLength; i++)
            {
                char c = text[i];

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    continue;
                }

                if (IsAllowedChar(c))
                {
                    kept[count++] = c;
                }
            }

            return new string(kept, 0, count);
        }

        /// <summary>The suffix shares the length cap with the name, so a long name shrinks before #2 is added.</summary>
        public static string WithSuffix(string nick, int number)
        {
            string suffix = "#" + number;
            string baseNick = nick ?? string.Empty;
            int room = MaxLength - suffix.Length;

            if (baseNick.Length > room)
            {
                baseNick = baseNick.Substring(0, room);
            }

            return baseNick + suffix;
        }

        /// <summary>A nick nobody in the room has yet, or null when the numbering ran out.</summary>
        public static string FirstFree(string nick, ICollection<string> taken)
        {
            if (nick == null || taken == null)
            {
                return null;
            }

            if (!taken.Contains(nick))
            {
                return nick;
            }

            for (int number = 2; number <= MaxSuffixNumber; number++)
            {
                string candidate = WithSuffix(nick, number);
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsAllowedChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }
    }
}
