namespace ReplayLibrary.Convert;

/// <summary>Calls Replay Converter when that plugin is loaded.</summary>
internal static class ReplayConvertClient
{
    private const string PluginName = "ReplayConverter";

    internal static bool IsLoaded =>
        PluginServices.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.InternalName == PluginName && plugin.IsLoaded);

    internal static bool IsAuthenticated
    {
        get
        {
            if (!IsLoaded)
                return false;

            try
            {
                return PluginServices.PluginInterface.GetIpcSubscriber<bool>("ReplayConverter.IsAuthenticated").InvokeFunc();
            }
            catch (Exception e)
            {
                PluginServices.Log.Warning(e, "Replay Converter did not answer");
                return false;
            }
        }
    }

    internal static string? Convert(string source) => Invoke("ReplayConverter.Convert", source);

    internal static string? Anonymize(string source, bool replace)
    {
        try
        {
            var path = PluginServices.PluginInterface.GetIpcSubscriber<string, bool, string>("ReplayConverter.Anonymize").InvokeFunc(source, replace);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Replay Converter call failed");
            return null;
        }
    }

    internal static string LastReport()
    {
        try
        {
            return PluginServices.PluginInterface.GetIpcSubscriber<string>("ReplayConverter.LastReport").InvokeFunc();
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Replay Converter did not report");
            return "";
        }
    }

    private static string? Invoke(string name, string source)
    {
        try
        {
            var path = PluginServices.PluginInterface.GetIpcSubscriber<string, string>(name).InvokeFunc(source);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Replay Converter call failed");
            return null;
        }
    }
}
