using IronNestFCS.Abstractions;

namespace IronNestFCS.Logic.FCS;

public static class BulletTypeExtensions
{
    /// <summary>
    /// The game-facing BulletType may still expose the legacy PLCM identifier. The FCS domain and UI
    /// use the canonical PCLM spelling while preserving numeric shell identity (13) at the boundary.
    /// </summary>
    public static string DisplayName(this BulletType type)
    {
        return (int)type == (int)ShellTypeCode.PCLM ? "PCLM" : type.ToString();
    }
}
