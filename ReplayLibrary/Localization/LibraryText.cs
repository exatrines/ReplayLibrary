namespace ReplayLibrary;

internal static class LibraryText
{
    public static void Register() =>
        MirageUi.I18n.RegisterEmbedded(typeof(LibraryText).Assembly, "ReplayLibrary.Localization");

    public static string Get(string key) => MirageUi.I18n.Get(key);

    public static string Format(string key, params object?[] args) => MirageUi.I18n.Format(key, args);
}
