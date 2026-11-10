namespace PvzMP.Core.Session
{
    /// <summary>
    /// One question at a time, answered when the player presses the dialog's own confirm button.
    /// The shipping implementation is a menu the game already ships (Core/GameAPI/NativePromptScreen);
    /// the tests drive a fake, which is the only reason this interface is short.
    /// </summary>
    public interface IPromptScreen
    {
        bool IsOpen { get; }

        void Show(string title, string prefill);

        void Close();

        /// <summary>True exactly once per confirmation, carrying the box as it was at that moment.</summary>
        bool TryTakeAnswer(out string text);
    }
}
