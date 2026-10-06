using System;

namespace LinkLauncher.Services;

/// <summary>
/// 右ボタン操作の認識部分だけを保持する、OS APIに依存しない状態です。
/// </summary>
internal sealed class RightMouseGestureState
{
    internal const int GestureMinimumRise = 70;
    internal const int ChordMaximumDrift = 12;
    internal const ulong GestureMaximumDurationMs = 1400;

    private bool _isPending;
    private int _startX;
    private int _startY;
    private ulong _startTick;
    private bool _startedWithControl;
    private bool _directionAbandoned;

    internal bool IsPending => _isPending;
    internal int StartX => _startX;
    internal int StartY => _startY;

    internal bool TryBegin(int x, int y, ulong tick, bool controlPressed)
    {
        if (_isPending)
        {
            return false;
        }

        _isPending = true;
        _startX = x;
        _startY = y;
        _startTick = tick;
        _startedWithControl = controlPressed;
        _directionAbandoned = false;
        return true;
    }

    internal bool HasExceededMaximumDuration(ulong tick)
    {
        return _isPending && tick - _startTick > GestureMaximumDurationMs;
    }

    internal bool ShouldRestoreOnMove(
        int x,
        int y,
        ulong tick,
        bool chordEnabled,
        bool gestureEnabled)
    {
        if (!_isPending || _directionAbandoned)
        {
            return false;
        }

        long horizontalDrift = Math.Abs((long)x - _startX);
        long verticalDrift = Math.Abs((long)y - _startY);
        if (tick - _startTick > GestureMaximumDurationMs)
        {
            _directionAbandoned = true;
            return true;
        }

        bool chordStillPossible = chordEnabled
            && _startedWithControl
            && horizontalDrift <= ChordMaximumDrift
            && verticalDrift <= ChordMaximumDrift;
        if (chordStillPossible)
        {
            return false;
        }

        if (gestureEnabled)
        {
            // 小さな手ぶれは保留し、20pxを超えた時点で上向き優勢でなければ通常操作へ戻します。
            if (Math.Max(horizontalDrift, verticalDrift) < 20)
            {
                return false;
            }

            long upwardDistance = (long)_startY - y;
            if (upwardDistance > 0 && upwardDistance > horizontalDrift * 1.5)
            {
                return false;
            }

            _directionAbandoned = true;
            return true;
        }

        if (horizontalDrift > ChordMaximumDrift || verticalDrift > ChordMaximumDrift)
        {
            _directionAbandoned = true;
            return true;
        }

        return false;
    }

    internal RightMouseReleaseAction Release(
        int x,
        int y,
        ulong tick,
        bool chordEnabled,
        bool gestureEnabled)
    {
        if (!_isPending)
        {
            return RightMouseReleaseAction.Pass;
        }

        ulong duration = tick - _startTick;
        long horizontalDrift = Math.Abs((long)x - _startX);
        long verticalDrift = Math.Abs((long)y - _startY);
        long upwardDistance = (long)_startY - y;

        bool chordRecognized = !_directionAbandoned
            && chordEnabled
            && _startedWithControl
            && duration <= GestureMaximumDurationMs
            && horizontalDrift <= ChordMaximumDrift
            && verticalDrift <= ChordMaximumDrift;

        bool gestureRecognized = !_directionAbandoned
            && gestureEnabled
            && duration <= GestureMaximumDurationMs
            && upwardDistance >= GestureMinimumRise
            && upwardDistance > horizontalDrift * 1.5;

        Reset();
        return chordRecognized || gestureRecognized
            ? RightMouseReleaseAction.Recognized
            : RightMouseReleaseAction.ReplayClick;
    }

    internal bool Cancel()
    {
        bool wasPending = _isPending;
        Reset();
        return wasPending;
    }

    internal void Reset()
    {
        _isPending = false;
        _startX = 0;
        _startY = 0;
        _startTick = 0;
        _startedWithControl = false;
        _directionAbandoned = false;
    }
}

internal enum RightMouseReleaseAction
{
    Pass,
    ReplayClick,
    Recognized
}
