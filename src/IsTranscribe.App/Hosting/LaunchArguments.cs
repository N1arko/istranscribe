using IsTranscribe.Host.Routing;

namespace IsTranscribe.App.Hosting;

public sealed record LaunchArguments(bool IsAutoStart)
{
    public static LaunchArguments Parse(IEnumerable<string> args)
    {
        var isAutoStart = args.Any(static arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));
        return new LaunchArguments(isAutoStart);
    }

    public HostLaunchContext ToLaunchContext() =>
        new(IsAutoStart ? HostLaunchOrigin.AutoStart : HostLaunchOrigin.UserLaunch);
}
