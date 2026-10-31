namespace CamRemote;

/// <summary>What Sony's SDK licence requires people to agree to before using an app built on it. Shown once.</summary>
public static class Agreement
{
    public const int Version = 1; // raise this if the text changes, so everyone sees it again

    public const string Text =
        "CamRemote is an independent app. It isn't made, endorsed or supported by Sony.\n\n" +
        "Sony's terms say that a camera controlled through an app like this one is no longer covered by its manufacturer warranty.\n\n" +
        "They also prohibit using this app or the camera for weapons, or for military activities such as intelligence, surveillance or reconnaissance.\n\n" +
        "By continuing, you agree to this.";
}
