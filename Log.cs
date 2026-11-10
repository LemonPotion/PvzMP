using System;
using System.Runtime.CompilerServices;
using MelonLoader;

namespace PvzMP
{
    public enum LogLevel
    {
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Single logging entry point. Everything the mod prints goes through here.
    /// MelonLoader already prefixes every line with the mod name, so only the area tag is added.
    /// </summary>
    public static class Log
    {
        /// <summary>
        /// Replaced outside the game so the transport can be tested on its own: the default target is
        /// MelonLogger, which only exists once MelonLoader is running. Set once at startup, never per call.
        /// </summary>
        public static Action<LogLevel, string> Sink;

        public static void Info(string area, string message)
        {
            Write(LogLevel.Info, "[" + area + "] " + message);
        }

        public static void Warn(string area, string message)
        {
            Write(LogLevel.Warn, "[" + area + "] " + message);
        }

        public static void Error(string area, string message)
        {
            Write(LogLevel.Error, "[" + area + "] " + message);
        }

        /// <summary>Every native call and every networked action is reported through this area.</summary>
        public static void Api(string message)
        {
            Info("api", message);
        }

        public static void Net(string message)
        {
            Info("net", message);
        }

        private static void Write(LogLevel level, string text)
        {
            Action<LogLevel, string> sink = Sink;
            if (sink != null)
            {
                sink(level, text);
                return;
            }

            ToMelonLogger(level, text);
        }

        /// <summary>
        /// Kept out of Write on purpose: merely compiling a method body resolves its type references, so
        /// a sink-only host (the test runner, where MelonLoader.dll is not present) would fail on the
        /// first log line if the reference sat in the method it also executes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ToMelonLogger(LogLevel level, string text)
        {
            if (level == LogLevel.Error)
            {
                MelonLogger.Error(text);
            }
            else if (level == LogLevel.Warn)
            {
                MelonLogger.Warning(text);
            }
            else
            {
                MelonLogger.Msg(text);
            }
        }
    }
}
