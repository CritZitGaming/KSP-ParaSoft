using System;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// Every line this mod writes to KSP.log goes through here, tagged so it can be
    /// grepped out of a two-hundred-mod log. Debug output only appears with verbose
    /// logging on, because a canopy per part per event adds up fast.
    /// </summary>
    internal static class Log
    {
        private const string Tag = "[ParaSoft] ";

        internal static bool Verbose;

        internal static void Info(string message)
        {
            Debug.Log(Tag + message);
        }

        internal static void Info(string format, params object[] args)
        {
            Debug.Log(Tag + string.Format(format, args));
        }

        internal static void Warn(string message)
        {
            Debug.LogWarning(Tag + message);
        }

        internal static void Warn(string format, params object[] args)
        {
            Debug.LogWarning(Tag + string.Format(format, args));
        }

        internal static void Error(string message)
        {
            Debug.LogError(Tag + message);
        }

        /// <summary>
        /// An exception that was caught and handled. Logged unconditionally: a swallowed
        /// exception that leaves no trace is how a mod earns a reputation for "just not
        /// working sometimes".
        /// </summary>
        internal static void Exception(string context, Exception e)
        {
            Debug.LogError(Tag + context + ": " + e);
        }

        internal static void Debug_(string format, params object[] args)
        {
            if (Verbose) Debug.Log(Tag + string.Format(format, args));
        }
    }
}
