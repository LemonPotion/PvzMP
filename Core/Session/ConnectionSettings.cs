namespace PvzMP.Core.Session
{
    /// <summary>What the dialog collects before a session can exist.</summary>
    public sealed class ConnectionSettings
    {
        public bool AsHost { get; set; }

        /// <summary>Empty for the host, which never connects anywhere.</summary>
        public string Host { get; set; }

        public int Port { get; set; }

        public string Nick { get; set; }

        public override string ToString()
        {
            return (AsHost ? "host" : "join " + Host + ":" + Port) + " as " + Nick;
        }
    }
}
