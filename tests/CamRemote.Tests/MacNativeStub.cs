namespace CamRemote;

/// <summary>Stands in for the Keychain when Settings is built outside Windows for the tests.</summary>
internal static class MacNative
{
    private static readonly Dictionary<(string, string), string> Saved = new();

    public static string LoadSecret(string service, string account)
    {
        lock (Saved) return Saved.GetValueOrDefault((service, account), "");
    }

    public static void SaveSecret(string service, string account, string secret)
    {
        lock (Saved)
        {
            foreach (var key in Saved.Keys.Where(k => k.Item1 == service).ToList()) Saved.Remove(key);
            if (secret.Length > 0) Saved[(service, account)] = secret;
        }
    }
}
