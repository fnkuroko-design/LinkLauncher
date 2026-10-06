using System;
using LinkLauncher.Models;

namespace LinkLauncher.Services;

internal enum MouseActivationButton
{
    Middle,
    Right,
    Left,
    XButton1,
    XButton2
}

internal enum MouseActivationReleaseAction
{
    Pass,
    ReplayClick
}

/// <summary>
/// マウスの先行ボタンと、移動による通常ドラッグへの復帰状態を保持します。
/// </summary>
internal sealed class MouseActivationState
{
    internal const int DragMovementThreshold = 5;

    private bool _isPending;
    private bool _movementDetected;
    private MouseActivationButton _button;
    private int _startX;
    private int _startY;

    internal bool IsPending => _isPending;
    internal bool IsStationary => _isPending && !_movementDetected;
    internal MouseActivationButton Button => _button;
    internal int StartX => _startX;
    internal int StartY => _startY;

    internal bool TryBegin(MouseActivationButton button, int x, int y)
    {
        if (_isPending || button is not (MouseActivationButton.Middle or MouseActivationButton.Right))
        {
            return false;
        }

        _isPending = true;
        _movementDetected = false;
        _button = button;
        _startX = x;
        _startY = y;
        return true;
    }

    internal bool IsExpectedChord(MouseActivationPattern pattern, MouseActivationButton secondButton)
    {
        if (!IsStationary)
        {
            return false;
        }

        return pattern switch
        {
            MouseActivationPattern.MiddleThenRight =>
                _button == MouseActivationButton.Middle && secondButton == MouseActivationButton.Right,
            MouseActivationPattern.RightThenLeft =>
                _button == MouseActivationButton.Right && secondButton == MouseActivationButton.Left,
            _ => false
        };
    }

    internal bool ShouldRestoreOnMove(int x, int y)
    {
        if (!IsStationary)
        {
            return false;
        }

        long horizontal = Math.Abs((long)x - _startX);
        long vertical = Math.Abs((long)y - _startY);
        if (horizontal <= DragMovementThreshold && vertical <= DragMovementThreshold)
        {
            return false;
        }

        _movementDetected = true;
        return true;
    }

    internal MouseActivationReleaseAction Release()
    {
        if (!_isPending)
        {
            return MouseActivationReleaseAction.Pass;
        }

        MouseActivationReleaseAction action = _movementDetected
            ? MouseActivationReleaseAction.Pass
            : MouseActivationReleaseAction.ReplayClick;
        Reset();
        return action;
    }

    internal bool Cancel()
    {
        bool wasPending = _isPending;
        Reset();
        return wasPending;
    }

    private void Reset()
    {
        _isPending = false;
        _movementDetected = false;
        _button = default;
        _startX = 0;
        _startY = 0;
    }
}
