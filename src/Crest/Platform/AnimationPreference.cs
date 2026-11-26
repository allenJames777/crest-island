using System.Runtime.InteropServices;

namespace Crest.Platform;

/// <summary>Windows' "Animation effects" setting (Settings, Accessibility, Visual effects).</summary>
internal static class AnimationPreference
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public static bool WindowsAnimationsOn() =>
        !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) || on;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
