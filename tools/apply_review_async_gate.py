from pathlib import Path
import re

executor = Path('IronNestFCS.Logic/Execution/FirePlanExecutor.cs')
text = executor.read_text(encoding='utf-8-sig')

def replace_once(old: str, new: str, label: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected exactly one match, found {count}')
    text = text.replace(old, new, 1)

replace_once(
    '    private const int FireSettlementBufferFrames = 3;\n',
    '    private const int FireSettlementBufferFrames = 3;\n'
    '    private const float ReviewLeadTimeBeforeArmSeconds = 1.2f;\n',
    'review lead constant')

replace_once(
    '    private bool _autoFireIssuedForWait;\n',
    '    private bool _autoFireIssuedForWait;\n'
    '    private bool _reviewProtocolDispatched;\n'
    '    private float _reviewProtocolStartedAt = float.NaN;\n',
    'review dispatch fields')

replace_once(
    '        _prepareCoroutines.Clear();\n        ClearAllFireWait();\n',
    '        _prepareCoroutines.Clear();\n'
    '        _reviewProtocolDispatched = false;\n'
    '        _reviewProtocolStartedAt = float.NaN;\n'
    '        ClearAllFireWait();\n',
    'dispose reset')

helpers = '''    private void DispatchReviewProtocolOnce()\n    {\n        if (_reviewProtocolDispatched)\n            return;\n\n        _reviewProtocolDispatched = true;\n        _reviewProtocolStartedAt = FcsRuntimeClock.Now;\n\n        // Review controls are protocol/visual actions, not firing gates. Dispatch each physical confirmation\n        // once for this Logic generation. FSC tracks every coroutine so F9 cancels outstanding work safely.\n        _fcs.TrackCoroutine(_fcs.TriggerConsole.ConfirmTask());\n        _fcs.TrackCoroutine(_fcs.TriggerConsole.ConfirmBullet());\n        _fcs.TrackCoroutine(_fcs.TriggerConsole.ConfirmRotation());\n        _fcs.TrackCoroutine(_fcs.TriggerConsole.ConfirmElevation());\n        _fcs.TrackCoroutine(_fcs.TriggerConsole.ReadyToFire());\n        MelonLogger.Msg("[FCS] TriggerConsole: dispatched one-shot review confirmations asynchronously");\n    }\n\n    private IEnumerator WaitForReviewLeadTime()\n    {\n        if (!_reviewProtocolDispatched || float.IsNaN(_reviewProtocolStartedAt))\n            yield break;\n\n        var remaining = ReviewLeadTimeBeforeArmSeconds - (FcsRuntimeClock.Now - _reviewProtocolStartedAt);\n        if (remaining > 0f)\n            yield return FcsRuntimeClock.WaitForSeconds(remaining);\n    }\n\n'''
replace_once(
    '    private IEnumerator RunShared(FirePlan plan)\n',
    helpers + '    private IEnumerator RunShared(FirePlan plan)\n',
    'review helper insertion')

replace_once(
    '                yield return _fcs.TriggerConsole.CompleteReviewProtocol();\n',
    '                DispatchReviewProtocolOnce();\n'
    '                yield return WaitForReviewLeadTime();\n',
    'review call replacement')

executor.write_text(text, encoding='utf-8')

trigger = Path('IronNestFCS.Logic/FCS/TriggerConsole.cs')
t = trigger.read_text(encoding='utf-8-sig')
t = t.replace('    private const float ParallelControlReadyTimeoutSeconds = 10f;\n', '')
t = t.replace('    private const float ReviewClickHoldSeconds = 0.1f;\n', '')
pattern = re.compile(
    r'\n    /// <summary>\n    /// Normal firing-solution review protocol\..*?\n    public IEnumerator CompleteReviewProtocol\(\) \{.*?\n    \}\n\n    public IEnumerator Arm\(LeftRight leftRight\)',
    re.S)
t, count = pattern.subn('\n    public IEnumerator Arm(LeftRight leftRight)', t, count=1)
if count != 1:
    raise SystemExit(f'CompleteReviewProtocol removal: expected one match, found {count}')
trigger.write_text(t, encoding='utf-8')

# Static invariants for this patch.
e = executor.read_text(encoding='utf-8')
tr = trigger.read_text(encoding='utf-8')
for required in (
    'ReviewLeadTimeBeforeArmSeconds = 1.2f',
    'DispatchReviewProtocolOnce();',
    'yield return WaitForReviewLeadTime();',
    '_fcs.TrackCoroutine(_fcs.TriggerConsole.ConfirmTask());',
    '_fcs.TrackCoroutine(_fcs.TriggerConsole.ReadyToFire());',
):
    if required not in e:
        raise SystemExit(f'missing invariant: {required}')
if 'CompleteReviewProtocol' in e or 'CompleteReviewProtocol' in tr:
    raise SystemExit('old blocking CompleteReviewProtocol still present')
