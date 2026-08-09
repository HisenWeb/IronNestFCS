using System.Collections;
using IronNestFCS.Logic.FCS;

namespace IronNestFCS.Logic.Infrastructure;

internal sealed class SceneExposureService {
    private readonly FSC _fcs;

    public SceneExposureService(FSC fcs) {
        _fcs = fcs;
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            foreach (var marker in _fcs.MapTable.GetAllFireMissionEntities()) {
                var visualRoot = marker.transform.FindChild("VisualRoot");
                visualRoot.gameObject.SetActive(true);
                visualRoot.FindChild("Info").gameObject.SetActive(true);
            }
            yield return FcsRuntimeClock.WaitForSeconds(1f);
        }
    }
}
