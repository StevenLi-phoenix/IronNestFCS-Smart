// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 主线程协程锁（非线程安全，仅限主线程协程使用）。
///
/// 带优先级队列: 等待者按 priority 降序放行(同优先级按到达顺序 FIFO)。
/// 用途示例: 弹道计算台上, 新任务解算(任务 priority, 紧急任务>=90)永远排在
/// 人工击发等待期的实时重调炮(低优先级 10)前面。
/// </summary>
public sealed class CoroutineLock {
    private bool _held;
    private long _ticketSeq;
    private readonly List<(int priority, long seq)> _waiters = new();

    private bool IsNext(int priority, long seq) {
        foreach (var w in _waiters)
            if (w.priority > priority || (w.priority == priority && w.seq < seq))
                return false;
        return true;
    }

    /// <summary>等待直到拿到锁。拿到后立即占用，调用方负责在 finally 里 Release。</summary>
    public IEnumerator Acquire() => Acquire(50);

    /// <summary>带优先级的锁等待: 高优先级先放行, 同级先到先得。</summary>
    public IEnumerator Acquire(int priority) {
        var seq = _ticketSeq++;
        var ticket = (priority, seq);
        _waiters.Add(ticket);
        try {
            // 每帧重试一次。持锁方在主线程推进，释放后下一帧队首协程即可抢到。
            while (_held || !IsNext(priority, seq))
                yield return null;
            _held = true;
        }
        finally {
            _waiters.Remove(ticket);
        }
    }

    /// <summary>
    /// 可取消的锁等待。等待期间或真正占锁前如果 shouldCancel 返回 true，就直接退出且不会占锁；
    /// 只有真正取得锁时才调用 onAcquired，调用方可据此区分“取得”与“取消”。
    /// </summary>
    public IEnumerator Acquire(Func<bool> shouldCancel, Action onAcquired) => Acquire(shouldCancel, onAcquired, 50);

    public IEnumerator Acquire(Func<bool> shouldCancel, Action onAcquired, int priority) {
        var seq = _ticketSeq++;
        var ticket = (priority, seq);
        _waiters.Add(ticket);
        try {
            while (_held || !IsNext(priority, seq)) {
                if (shouldCancel())
                    yield break;
                yield return null;
            }

            // 锁刚释放和本协程恢复之间也可能发生 F9 / 任务取消，因此占锁前再检查一次。
            if (shouldCancel())
                yield break;

            _held = true;
            onAcquired();
        }
        finally {
            _waiters.Remove(ticket);
        }
    }

    public void Release() {
        _held = false;
    }

    /// <summary>重绑定（热重载）时强制复位，防止上一轮异常残留导致死锁。</summary>
    public void Reset() {
        _held = false;
        _waiters.Clear();
    }
}
