namespace IronNestFCS.Logic.FCS;

public static class BulletTypeExtensions
{
    /// <summary>
    /// 玩家可见名称与游戏采购卡片保持一致；内部枚举/ShellId 仍使用 PLCM。
    /// </summary>
    public static string DisplayName(this BulletType type)
    {
        return type == BulletType.PLCM ? "PCLM" : type.ToString();
    }
}
