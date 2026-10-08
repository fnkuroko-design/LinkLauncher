using System;

namespace LinkLauncher.Services;

/// <summary>
/// マウス起動要求の押下、解放、UI側キャプチャをticketごとに一度だけ結び付けます。
/// </summary>
public sealed class MouseActivationCompletion
{
    private readonly object _gate = new();
    private long _ticketSequence;
    private long _currentTicket;
    private bool _hasCurrentTicket;
    private bool _isPending;
    private bool _isReleased;
    private bool _isCaptured;
    private Action? _callback;

    /// <summary>
    /// 解放通知またはUI側キャプチャのどちらかを待っているか返します。
    /// </summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _isPending;
            }
        }
    }

    /// <summary>
    /// 新しい要求を開始し、それ以前のticketとcallbackを無効にします。
    /// </summary>
    public long Begin()
    {
        lock (_gate)
        {
            unchecked
            {
                _ticketSequence++;
                if (_ticketSequence == 0)
                {
                    _ticketSequence++;
                }
            }

            _currentTicket = _ticketSequence;
            _hasCurrentTicket = true;
            _isPending = true;
            _isReleased = false;
            _isCaptured = false;
            _callback = null;
            return _currentTicket;
        }
    }

    /// <summary>
    /// ticketが現在の要求世代か返します。完了callbackの呼出し前も有効です。
    /// </summary>
    public bool IsCurrent(long ticket)
    {
        lock (_gate)
        {
            return IsCurrentUnsafe(ticket);
        }
    }

    /// <summary>
    /// callbackを登録します。解放済みならcallbackを一度だけ返します。
    /// nullを渡すと現在の要求をキャンセルします。
    /// </summary>
    public Action? Capture(long ticket, Action? callback)
    {
        lock (_gate)
        {
            if (!IsCurrentUnsafe(ticket))
            {
                return null;
            }

            if (callback is null)
            {
                InvalidateCurrentUnsafe();
                return null;
            }

            if (!_isPending || _isCaptured)
            {
                return null;
            }

            _isCaptured = true;
            if (_isReleased)
            {
                _isPending = false;
                return callback;
            }

            _callback = callback;
            return null;
        }
    }

    /// <summary>
    /// 呼出しボタンの解放を一度通知し、登録済みcallbackがあれば返します。
    /// </summary>
    public Action? Release()
    {
        lock (_gate)
        {
            if (!_hasCurrentTicket || !_isPending || _isReleased)
            {
                return null;
            }

            _isReleased = true;
            if (!_isCaptured)
            {
                return null;
            }

            Action? callback = _callback;
            _callback = null;
            _isPending = false;
            return callback;
        }
    }

    /// <summary>
    /// 要求を無効にし、保持しているcallbackを解放します。
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            InvalidateCurrentUnsafe();
        }
    }

    private bool IsCurrentUnsafe(long ticket) => _hasCurrentTicket && _currentTicket == ticket;

    private void InvalidateCurrentUnsafe()
    {
        _hasCurrentTicket = false;
        _isPending = false;
        _isReleased = false;
        _isCaptured = false;
        _callback = null;
    }
}
