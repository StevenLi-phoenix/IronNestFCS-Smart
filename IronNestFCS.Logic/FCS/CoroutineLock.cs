// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 协程级优先级互斥锁。MelonCoroutines 全部在 Unity 主线程上协作式调度，没有真正并发，
/// 因此一个 bool 加一份等待票列表足以实现互斥——不需要任何并发原语
/// （lock/Interlocked/SemaphoreSlim）。
///
/// 等待者按 (priority 降序, 登记序号升序) 排队：高优先级先取锁，同级 FIFO。
/// 全局优先级约定：规划期台解 / 执行期征用·扳机 = 任务 priority（默认 50，紧急 ≥90）；
/// 外部买卡请求 = 请求自带 priority；火药自动补货 = 20；人工击发等待期实时重调 = 10。
///
/// 用法（务必配 try/finally，保证协程被 Stop / yield break 时也能释放）：
/// <code>
/// yield return deskLock.Acquire();
/// try { /* 临界区，可含 yield return */ }
/// finally { deskLock.Release(); }
/// </code>
/// 迭代器被 MelonCoroutines.Stop 停掉时会 Dispose，finally 块照常执行 → 锁与票都不会泄漏。
///
/// 注：四个 Acquire 重载全部保留。外部 mod 用无参 <c>GetMethod("Acquire")</c> 反射取方法时会因此
/// 抛 AmbiguousMatchException；这是已知且已裁决的契约事实，外部应改用
/// <c>GetMethod("Acquire", Type.EmptyTypes)</c> 精确取无参重载，FCS 侧不为此新增别名方法。
/// </summary>
public sealed class CoroutineLock {
    /// <summary>无参 Acquire() 与无优先级的可取消重载使用的默认优先级。</summary>
    private const int DefaultPriority = 50;

    /// <summary>一张等待票。用引用类型是为了让 finally 里的 Remove 走引用相等，绝不误删同级同优先级的别人。</summary>
    private sealed class Ticket {
        public int Priority;
        public int Seq;
    }

    private readonly List<Ticket> _waiters = new();
    private bool _held;
    private int _ticketSeq;

    /// <summary>当前票是否轮到自己：没有更高优先级的票，也没有同级更早登记的票。</summary>
    private bool IsNext(int priority, int seq) {
        for (var i = 0; i < _waiters.Count; i++) {
            var waiter = _waiters[i];
            if (waiter.Priority > priority)
                return false;
            if (waiter.Priority == priority && waiter.Seq < seq)
                return false;
        }

        return true;
    }

    /// <summary>等待直到拿到锁（默认优先级 50）。拿到后立即占用，调用方负责在 finally 里 Release。</summary>
    public IEnumerator Acquire() {
        return Acquire(DefaultPriority);
    }

    /// <summary>按给定优先级等待直到拿到锁。拿到后立即占用，调用方负责在 finally 里 Release。</summary>
    public IEnumerator Acquire(int priority) {
        var seq = _ticketSeq++;
        var ticket = new Ticket { Priority = priority, Seq = seq };
        _waiters.Add(ticket);
        try {
            // 每帧重试一次。持锁方在主线程推进，释放后下一帧轮到的协程即可抢到。
            // 这里必须是 yield return null 而不是 WaitForSeconds / WaitUntilFocused：后者会给每次取锁
            // 引入额外延迟，并让锁等待被暂停/失焦阻塞，破坏“人工等待期重调 P10 让路”的实时性前提。
            while (_held || !IsNext(priority, seq)) {
                yield return null;
            }

            _held = true;
        }
        finally {
            // 协程被 Stop 时也要走到这里，否则残票会永久压住后来的同级/低优先级等待者。
            _waiters.Remove(ticket);
        }
    }

    /// <summary>
    /// 可取消的锁等待（默认优先级 50）。等待期间或真正占锁前如果 shouldCancel 返回 true，
    /// 就直接退出且不会占锁；只有真正取得锁时才调用 onAcquired，调用方可据此区分“取得”与“取消”。
    /// </summary>
    public IEnumerator Acquire(Func<bool> shouldCancel, Action onAcquired) {
        return Acquire(shouldCancel, onAcquired, DefaultPriority);
    }

    /// <summary>
    /// 带优先级的可取消锁等待。与 <see cref="Acquire(int)"/> 完全同构：同样登记票、同样按
    /// 优先级票队等待、同样在 finally 里移票。朴素的 <c>while (_held)</c> 写法会让可取消 acquire
    /// 插队越过已排队的高优先级等待者，并使 priority 参数彻底失效。
    /// </summary>
    public IEnumerator Acquire(Func<bool> shouldCancel, Action onAcquired, int priority) {
        var seq = _ticketSeq++;
        var ticket = new Ticket { Priority = priority, Seq = seq };
        _waiters.Add(ticket);
        try {
            // shouldCancel 在每次挂帧之前检查：入口即取消时不产生任何 yield。
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
            // 取消路径同样不得漏票。
            _waiters.Remove(ticket);
        }
    }

    public void Release() {
        _held = false;
    }

    /// <summary>重绑定（热重载）时强制复位，防止上一轮异常残留的持锁标志或等待票导致死锁。</summary>
    public void Reset() {
        _held = false;
        _waiters.Clear();
    }
}
