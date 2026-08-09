from pathlib import Path

p = Path('IronNestFCS.Logic/FSC.cs')
s = p.read_text(encoding='utf-8')

old = '''        var leftAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -left.Task.angel));
        var rightAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -right.Task.angel));
        var leftElevationDelta = Mathf.Abs(left.Task.elevation - leftElevation);
        var rightElevationDelta = Mathf.Abs(right.Task.elevation - rightElevation);
        var leftScore = leftAzimuthDelta + leftElevationDelta;
        var rightScore = rightAzimuthDelta + rightElevationDelta;

        _firePriorityLeftDetail =
            $"左T{left.Task.targetId}：{leftScore:F1}°（方{leftAzimuthDelta:F1} + 仰{leftElevationDelta:F1}）";
        _firePriorityRightDetail =
            $"右T{right.Task.targetId}：{rightScore:F1}°（方{rightAzimuthDelta:F1} + 仰{rightElevationDelta:F1}）";
'''

new = '''        var leftAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -left.Task.angel));
        var rightAzimuthDelta = Mathf.Abs(Mathf.DeltaAngle(currentAzimuth, -right.Task.angel));
        var leftElevationDelta = Mathf.Abs(left.Task.elevation - leftElevation);
        var rightElevationDelta = Mathf.Abs(right.Task.elevation - rightElevation);

        // Measured release-build slew rates are approximately AZ=4 deg/s and EL=2 deg/s.
        // Use azimuth degrees as the common time-equivalent unit: 1 degree of elevation costs the
        // same time as 2 degrees of azimuth. Since both axes move in parallel, readiness is gated
        // by the slower remaining axis rather than by the sum of both movements.
        var leftElevationEquivalent = leftElevationDelta * 2f;
        var rightElevationEquivalent = rightElevationDelta * 2f;
        var leftScore = Mathf.Max(leftAzimuthDelta, leftElevationEquivalent);
        var rightScore = Mathf.Max(rightAzimuthDelta, rightElevationEquivalent);

        _firePriorityLeftDetail =
            $"左T{left.Task.targetId}：{leftScore:F1}（方{leftAzimuthDelta:F1} / 仰{leftElevationDelta:F1}×2={leftElevationEquivalent:F1}）";
        _firePriorityRightDetail =
            $"右T{right.Task.targetId}：{rightScore:F1}（方{rightAzimuthDelta:F1} / 仰{rightElevationDelta:F1}×2={rightElevationEquivalent:F1}）";
'''

if old not in s:
    raise SystemExit('score block not found')
s = s.replace(old, new, 1)

s = s.replace(
    '$"(az {leftAzimuthDelta:F1}+el {leftElevationDelta:F1}) < Right T{right.Task.targetId}={rightScore:F1}° " +\n                $"(az {rightAzimuthDelta:F1}+el {rightElevationDelta:F1})";',
    '$"(az {leftAzimuthDelta:F1}, el {leftElevationDelta:F1}x2={leftElevationEquivalent:F1}) < Right T{right.Task.targetId}={rightScore:F1} " +\n                $"(az {rightAzimuthDelta:F1}, el {rightElevationDelta:F1}x2={rightElevationEquivalent:F1})";',
    1)
s = s.replace(
    '$"(az {rightAzimuthDelta:F1}+el {rightElevationDelta:F1}) < Left T{left.Task.targetId}={leftScore:F1}° " +\n                $"(az {leftAzimuthDelta:F1}+el {leftElevationDelta:F1})";',
    '$"(az {rightAzimuthDelta:F1}, el {rightElevationDelta:F1}x2={rightElevationEquivalent:F1}) < Left T{left.Task.targetId}={leftScore:F1} " +\n                $"(az {leftAzimuthDelta:F1}, el {leftElevationDelta:F1}x2={leftElevationEquivalent:F1})";',
    1)

if 'var leftScore = leftAzimuthDelta + leftElevationDelta;' in s:
    raise SystemExit('old additive score remains')
if 'Mathf.Max(leftAzimuthDelta, leftElevationEquivalent)' not in s:
    raise SystemExit('new max score missing')

p.write_text(s, encoding='utf-8')
