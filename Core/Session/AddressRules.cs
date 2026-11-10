using System.Globalization;

namespace PvzMP.Core.Session
{
    /// <summary>
    /// What the address and port prompts accept. Deliberately permissive on the host part: a name that
    /// does not resolve or a machine that does not answer is a connect failure that the log already
    /// explains by itself, while strict grammar here would reject valid addresses.
    /// </summary>
    public static class AddressRules
    {
        public const int DefaultPort = 7777;

        /// <summary>Same ceiling the wire puts on every string, so a prompt can never hold an oversized host.</summary>
        public const int MaxHostLength = 64;

        public static bool IsPort(int port)
        {
            return port >= 1 && port <= 65535;
        }

        /// <summary>True when the box holds exactly a port number; anything else leaves the default alone.</summary>
        public static bool TryPort(string text, out int port)
        {
            port = 0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && IsPort(port);
        }

        /// <summary>
        /// Host part of the address: an ipv4 dotted quad or a name. TcpClient is handed this string
        /// untouched, so only characters that cannot belong to either are dropped.
        /// </summary>
        public static string CleanHost(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            char[] kept = new char[text.Length];
            int count = 0;

            for (int i = 0; i < text.Length && count < MaxHostLength; i++)
            {
                char c = text[i];

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    continue;
                }

                if (IsHostChar(c))
                {
                    kept[count++] = c;
                }
            }

            return new string(kept, 0, count);
        }

        /// <summary>An empty host is never worth a connect attempt: the dialog asks again instead.</summary>
        public static bool IsUsableHost(string host)
        {
            return !string.IsNullOrEmpty(host);
        }

        private static bool IsHostChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '.' || c == '-';
        }
    }
}
