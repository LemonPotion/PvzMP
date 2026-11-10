namespace PvzMP.Core.Session
{
    /// <summary>
    /// One cell's plant as a lawn's own growth made it: the cell, the kind that stood there and the kind
    /// that stands there now. These are the four numbers one lawn needs to say to the rest of the room
    /// about a fertilizer that did more than heal, and they are the whole of what travels.
    /// </summary>
    public struct Growth
    {
        public int Column;
        public int Row;
        public int OldType;
        public int NewType;

        public Growth(int column, int row, int oldType, int newType)
        {
            Column = column;
            Row = row;
            OldType = oldType;
            NewType = newType;
        }
    }
}
