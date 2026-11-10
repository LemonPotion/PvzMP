using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// What one reference argument looks like on the wire. A reference is never an address: an address from
    /// another machine means nothing here, so a reference travels as the only things that do mean something
    /// in both games - the room's id for an entity, the scene path for a scene object, the name for an
    /// asset, and the class that keeps a tool in its static `Instance`. The live object is looked up where
    /// the frame lands, never where it was written.
    /// </summary>
    public enum RefKind
    {
        /// <summary>The nothing reference: a null argument the native method expects.</summary>
        None,

        /// <summary>#7 - an entity the room minted, looked up in the entity ledger.</summary>
        Entity,

        /// <summary>/Board/SunCounter - a scene object, found by walking the hierarchy.</summary>
        ScenePath,

        /// <summary>@Peashooter - an asset, found by name under Resources.</summary>
        Asset,

        /// <summary>!Hammer - the singleton a class keeps in its static `Instance`, looked up per lawn.</summary>
        Singleton
    }

    /// <summary>
    /// The adapter registry for one native call: the measured parameter categories of the row
    /// (CoverageFacts.Cats, from tools/re/pvz-index.py) decide how every argument is written and read, so
    /// a new synced feature never needs a payload class. The letters are the atlas's own:
    /// i int, e enum (an int32 on the wire), b bool, f float, s string, o object reference,
    /// t value type by its numeric fields, a array of references.
    ///
    /// Everything else - d delegate, p pointer, g shared generic, k class reference, ? unrecognized -
    /// cannot be a value at all, and saying so here is what keeps a row from quietly sending a number
    /// where the game wanted a function pointer.
    /// </summary>
    public static class CallArgs
    {
        /// <summary>Categories with no wire shape; a method with one in its list is not a door.</summary>
        public const string UnsendableCats = "dpgk?";

        /// <summary>The largest argument vector a peer is allowed to hand us.</summary>
        public const int MaxArgs = 16;

        /// <summary>The longest array or value type a peer is allowed to name in one argument.</summary>
        public const int MaxElements = 64;

        public static string Key(string classToken, string method, int arity)
        {
            return Arbiter.Key(classToken, method, arity);
        }

        /// <summary>Null when every category can travel, otherwise the letter that cannot.</summary>
        public static string UnsendableIn(string cats)
        {
            if (cats == null)
            {
                return null;
            }

            for (int i = 0; i < cats.Length; i++)
            {
                if (UnsendableCats.IndexOf(cats[i]) >= 0)
                {
                    return cats[i].ToString();
                }
            }

            return null;
        }

        public static bool IsSendable(string cats)
        {
            return UnsendableIn(cats) == null;
        }

        /// <summary>How many values an argument vector for these categories has to hold.</summary>
        public static int Count(string cats)
        {
            return cats == null ? 0 : cats.Length;
        }

        /// <summary>
        /// A reference to an entity the room minted. Negative ids are refused: the ledger never hands one
        /// out, so a negative here would be a bug at the call site reaching a lawn as a missing plant.
        /// </summary>
        public static string Entity(int netId)
        {
            if (netId < 0)
            {
                throw new FormatException("an entity reference needs a room id, got " + netId);
            }

            return "#" + netId.ToString(CultureInfo.InvariantCulture);
        }

        public static string ScenePath(string path)
        {
            return ScenePrefix + CheckLength(path ?? string.Empty, "scene path");
        }

        public static string Asset(string name)
        {
            return "@" + CheckLength(name ?? string.Empty, "asset name");
        }

        /// <summary>
        /// The game's own singleton of a class, by the class' simple name. A tool is not an entity the room
        /// numbers and not a scene path the mod can walk to: every lawn builds its own, and what a frame
        /// can say is which class keeps it. Proof: ref/EVIDENCE.md E60.
        /// </summary>
        public static string Singleton(string className)
        {
            return "!" + CheckLength(className ?? string.Empty, "class name");
        }

        /// <summary>The reference a native method is handed when the argument is null.</summary>
        public const string None = "~";

        private const string ScenePrefix = "/";

        /// <summary>
        /// The one form a reference token has to keep so the reader can tell which of the five lookups it
        /// wants. Anything else throws, because a token without a prefix is exactly how an address from
        /// the sending machine would sneak onto the wire.
        /// </summary>
        public static RefKind KindOf(string token)
        {
            if (string.IsNullOrEmpty(token) || token == None)
            {
                return RefKind.None;
            }

            switch (token[0])
            {
                case '#':
                    return RefKind.Entity;
                case '/':
                    return RefKind.ScenePath;
                case '@':
                    return RefKind.Asset;
                case '!':
                    if (!IsClassName(token.Substring(1)))
                    {
                        throw new FormatException("singleton token '" + token + "' carries no class name");
                    }

                    return RefKind.Singleton;
                default:
                    throw new FormatException("reference token '" + token + "' names none of #entity, "
                        + "/path, @asset or !Class");
            }
        }

        /// <summary>
        /// A simple type name: the game's class names are ASCII letters, digits and underscores. A
        /// non-ASCII letter passes `char.IsLetter` but no class of this build is spelled that way, and the
        /// log this token may reach is ASCII only, so a name no lookup can answer is refused here.
        /// </summary>
        private static bool IsClassName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !IsNameStart(name[0]))
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!IsNameStart(c) && !(c >= '0' && c <= '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsNameStart(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
        }

        /// <summary>The part of the token after its prefix: the net id, the path or the asset name.</summary>
        public static string ValueOf(string token)
        {
            KindOf(token);
            return token == None ? string.Empty : token.Substring(1);
        }

        /// <summary>The room id an entity token carries; throws for every other kind of token.</summary>
        public static int NetIdOf(string token)
        {
            if (KindOf(token) != RefKind.Entity)
            {
                throw new FormatException("reference token '" + token + "' is not an entity");
            }

            int netId;
            if (!int.TryParse(ValueOf(token), NumberStyles.None, CultureInfo.InvariantCulture, out netId))
            {
                throw new FormatException("entity token '" + token + "' carries no plain id");
            }

            return netId;
        }

        public static string Encode(string cats, params object[] values)
        {
            object[] args = values == null ? new object[0] : values;
            string unsendable = UnsendableIn(cats);
            if (unsendable != null)
            {
                throw new FormatException("category " + unsendable + " has no wire shape, so a call "
                    + "with " + Count(cats) + " parameter(s) cannot be written");
            }

            if (args.Length != Count(cats))
            {
                throw new FormatException("the call carries " + args.Length + " argument(s) for "
                    + Count(cats) + " parameter(s) [" + cats + "]");
            }

            StringBuilder json = new StringBuilder("[");
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                Write(json, cats[i], args[i], i);
            }

            json.Append(']');
            if (json.Length > Protocol.MaxJsonChars)
            {
                throw new FormatException("the argument vector is " + json.Length + " chars, the cap is "
                    + Protocol.MaxJsonChars);
            }

            return json.ToString();
        }

        public static object[] Decode(string cats, string json)
        {
            string unsendable = UnsendableIn(cats);
            if (unsendable != null)
            {
                throw new FormatException("category " + unsendable + " has no wire shape, so the row "
                    + "cannot be applied");
            }

            if (json == null)
            {
                throw new FormatException("the call carried no arguments at all");
            }

            List<object> values = new List<object>();
            JsonDocumentOptions options = new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            };

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, options);
            }
            catch (Exception e)
            {
                // A peer's bytes are not trusted to be JSON: everything the reader throws becomes the
                // one exception the inbox logs, so a hostile frame is a refusal and never a crash.
                throw new FormatException("the argument vector is not readable json: " + e.Message);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("the argument vector is a " + root.ValueKind
                        + ", not an array");
                }

                foreach (JsonElement element in root.EnumerateArray())
                {
                    if (values.Count >= MaxArgs)
                    {
                        throw new FormatException("the call carries more than " + MaxArgs
                            + " arguments, which the table has no row for");
                    }

                    values.Add(Read(element, cats == null ? '?' : cats[values.Count], values.Count));
                }
            }

            if (values.Count != Count(cats))
            {
                throw new FormatException("the call carries " + values.Count + " argument(s) for "
                    + Count(cats) + " parameter(s) [" + cats + "]");
            }

            return values.ToArray();
        }

        /// <summary>One line for the log: the category, then the value, never the whole decoded vector.</summary>
        public static string Describe(string cats, object[] values)
        {
            if (string.IsNullOrEmpty(cats))
            {
                return "()";
            }

            StringBuilder text = new StringBuilder("(");
            for (int i = 0; i < cats.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }

                object value = values == null || i >= values.Length ? null : values[i];
                text.Append(cats[i]).Append('=').Append(Short(value));
            }

            return text.Append(')').ToString();
        }

        // ---- one category, one shape -----------------------------------------

        private static void Write(StringBuilder json, char cat, object value, int index)
        {
            switch (cat)
            {
                case 'i':
                case 'e':
                    json.Append(ToLong(value, index, cat));
                    return;
                case 'b':
                    json.Append(ToBool(value, index) ? "true" : "false");
                    return;
                case 'f':
                    json.Append(ToDouble(value, index, "float")
                        .ToString("R", CultureInfo.InvariantCulture));
                    return;
                case 's':
                    WriteString(json, ToString(value, index, "string"), "string");
                    return;
                case 'o':
                    WriteString(json, ToToken(value, index, "reference"), "reference");
                    return;
                case 't':
                    WriteNumbers(json, ToArray(value, index), "value type");
                    return;
                case 'a':
                    WriteTokens(json, ToArray(value, index), index);
                    return;
                default:
                    throw new FormatException("argument " + index + " has category " + cat
                        + ", which the adapter registry does not know");
            }
        }

        private static object Read(JsonElement element, char cat, int index)
        {
            switch (cat)
            {
                case 'i':
                case 'e':
                    return ReadInt(element, index);
                case 'b':
                    return ReadBool(element, index);
                case 'f':
                    return ReadDouble(element, index);
                case 's':
                    return ReadString(element, index, "string");
                case 'o':
                    return ReadToken(element, index);
                case 't':
                    return ReadNumbers(element, index);
                case 'a':
                    return ReadTokenArray(element, index);
                default:
                    throw new FormatException("argument " + index + " has category " + cat
                        + ", which the adapter registry does not know");
            }
        }

        private static long ReadInt(JsonElement element, int index)
        {
            try
            {
                return element.GetInt64();
            }
            catch (Exception e)
            {
                throw new FormatException("argument " + index + " is not an integer: " + e.Message);
            }
        }

        private static bool ReadBool(JsonElement element, int index)
        {
            if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
            {
                throw new FormatException("argument " + index + " is a " + element.ValueKind
                    + ", not true or false");
            }

            return element.GetBoolean();
        }

        private static double ReadDouble(JsonElement element, int index)
        {
            try
            {
                return element.GetDouble();
            }
            catch (Exception e)
            {
                throw new FormatException("argument " + index + " is not a number: " + e.Message);
            }
        }

        private static string ReadString(JsonElement element, int index, string what)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("argument " + index + " is a " + what
                    + " the reader cannot find: it is " + element.ValueKind);
            }

            return CheckLength(element.GetString(), "argument " + index + " " + what);
        }

        /// <summary>
        /// A null reference is written as the token for nothing rather than as JSON null, so the reader
        /// sees the same shape for every object argument and the call site can ask one question.
        /// </summary>
        private static string ReadToken(JsonElement element, int index)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                return None;
            }

            string token = ReadString(element, index, "reference");

            // The prefix is the whole contract: it is what tells the reader which of the four lookups to
            // run, and an address from the sending machine has no prefix to be found with.
            KindOf(token);
            return token;
        }

        private static object[] ReadNumbers(JsonElement element, int index)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("argument " + index + " is a value type written as a "
                    + element.ValueKind + ", not its numbers");
            }

            List<object> numbers = new List<object>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (numbers.Count >= MaxElements)
                {
                    throw new FormatException("argument " + index + " holds more than " + MaxElements
                        + " numbers");
                }

                numbers.Add(ReadDouble(item, index));
            }

            return numbers.ToArray();
        }

        private static object[] ReadTokenArray(JsonElement element, int index)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("argument " + index + " is an array written as a "
                    + element.ValueKind);
            }

            List<object> tokens = new List<object>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (tokens.Count >= MaxElements)
                {
                    throw new FormatException("argument " + index + " holds more than " + MaxElements
                        + " references");
                }

                tokens.Add(ReadToken(item, index));
            }

            return tokens.ToArray();
        }

        // ---- writing --------------------------------------------------------

        private static void WriteString(StringBuilder json, string text, string what)
        {
            json.Append('"').Append(Escape(CheckLength(text, what))).Append('"');
        }

        private static void WriteNumbers(StringBuilder json, object[] items, string what)
        {
            if (items.Length == 0)
            {
                throw new FormatException("a " + what + " with no numbers cannot be a value");
            }

            json.Append('[');
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                json.Append(ToDouble(items[i], i, what).ToString("R", CultureInfo.InvariantCulture));
            }

            json.Append(']');
        }

        private static void WriteTokens(StringBuilder json, object[] items, int index)
        {
            json.Append('[');
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                WriteString(json, ToToken(items[i], index, "array element"), "reference");
            }

            json.Append(']');
        }

        private static string CheckLength(string text, string what)
        {
            if (text == null)
            {
                throw new FormatException(what + " is null where a value was expected");
            }

            if (text.Length > Protocol.MaxTextChars)
            {
                throw new FormatException(what + " is " + text.Length + " chars, the cap is "
                    + Protocol.MaxTextChars);
            }

            return text;
        }

        private static string Escape(string text)
        {
            StringBuilder escaped = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\\')
                {
                    escaped.Append('\\').Append(c);
                }
                else if (c == '\n')
                {
                    escaped.Append("\\n");
                }
                else if (c == '\r')
                {
                    escaped.Append("\\r");
                }
                else if (c == '\t')
                {
                    escaped.Append("\\t");
                }
                else if (c < ' ' || c > 126)
                {
                    escaped.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                {
                    escaped.Append(c);
                }
            }

            return escaped.ToString();
        }

        // ---- the call site's values into the registry's shapes ---------------

        private static long ToLong(object value, int index, char cat)
        {
            if (value == null)
            {
                throw new FormatException("argument " + index + " of category " + cat + " is null");
            }

            try
            {
                if (value is bool)
                {
                    throw new FormatException("argument " + index + " is a bool, not an int");
                }

                if (value is string text)
                {
                    return long.Parse(text, CultureInfo.InvariantCulture);
                }

                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new FormatException("argument " + index + " is not an int: " + e.GetType().Name);
            }
        }

        private static bool ToBool(object value, int index)
        {
            if (value is bool flag)
            {
                return flag;
            }

            if (value is string text)
            {
                bool parsed;
                if (bool.TryParse(text, out parsed))
                {
                    return parsed;
                }
            }

            throw new FormatException("argument " + index + " is a " + Name(value) + ", not a bool");
        }

        private static double ToDouble(object value, int index, string what)
        {
            if (value == null)
            {
                throw new FormatException("argument " + index + " of " + what + " is null");
            }

            if (value is bool)
            {
                throw new FormatException("argument " + index + " is a bool, not a number");
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                throw new FormatException("argument " + index + " is not a number (" + what + "): "
                    + e.GetType().Name);
            }
        }

        private static string ToString(object value, int index, string what)
        {
            string text = value as string;
            if (text != null)
            {
                return text;
            }

            if (value == null)
            {
                throw new FormatException("argument " + index + " is null where a " + what
                    + " was expected");
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string ToToken(object value, int index, string what)
        {
            string text = value as string;
            if (text == null)
            {
                if (value == null)
                {
                    return None;
                }

                if (value is int)
                {
                    return Entity((int)value);
                }

                throw new FormatException("argument " + index + " is a " + Name(value) + " where a "
                    + what + " reference was expected: pass the token, not the object");
            }

            KindOf(text);
            return text;
        }

        private static object[] ToArray(object value, int index)
        {
            object[] items = value as object[];
            if (items != null)
            {
                if (items.Length > MaxElements)
                {
                    throw new FormatException("argument " + index + " holds " + items.Length
                        + " elements, the cap is " + MaxElements);
                }

                return items;
            }

            string[] texts = value as string[];
            if (texts != null)
            {
                if (texts.Length > MaxElements)
                {
                    throw new FormatException("argument " + index + " holds " + texts.Length
                        + " elements, the cap is " + MaxElements);
                }

                List<object> boxed = new List<object>(texts.Length);
                for (int i = 0; i < texts.Length; i++)
                {
                    boxed.Add(texts[i]);
                }

                return boxed.ToArray();
            }

            IEnumerable<object> sequence = value as IEnumerable<object>;
            if (sequence != null)
            {
                List<object> collected = new List<object>();
                foreach (object item in sequence)
                {
                    if (collected.Count >= MaxElements)
                    {
                        throw new FormatException("argument " + index + " holds more than " + MaxElements
                            + " elements");
                    }

                    collected.Add(item);
                }

                return collected.ToArray();
            }

            throw new FormatException("argument " + index + " is a " + Name(value)
                + " where an array was expected");
        }

        private static string Name(object value)
        {
            return value == null ? "null" : value.GetType().Name;
        }

        private static string Short(object value)
        {
            if (value == null)
            {
                return "-";
            }

            object[] items = value as object[];
            if (items != null)
            {
                return "[" + items.Length + "]";
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);

            // Whatever a peer put in a string argument ends up here, and the log file is the mod's only
            // witness: control characters would forge lines and non-ASCII bytes would break the reader.
            StringBuilder safe = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length && safe.Length < 24; i++)
            {
                char c = text[i];
                safe.Append(c < ' ' || c > 126 ? '?' : c);
            }

            return text.Length > 24 ? safe.Append("...").ToString() : safe.ToString();
        }
    }
}
