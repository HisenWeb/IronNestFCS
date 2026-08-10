namespace IronNestFCS.Abstractions;

/// <summary>
/// Host 与可热重载 Logic 之间的唯一跨 AssemblyLoadContext 契约。
/// </summary>
public interface IFcsModule
{
    /// <summary>重载后调用一次。Host services 位于稳定上下文，F9 不会销毁。</summary>
    bool Initialize(IFcsHostServices hostServices);

    void Update();

    void OnGui();

    void Shutdown();
}
