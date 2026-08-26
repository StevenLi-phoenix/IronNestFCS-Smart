using System.Collections;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Infrastructure;

/// <summary>
/// One externally submitted requisition card purchase. Plain public mutable fields on purpose:
/// external callers (the agent bridge) build these by hand and fill in only what they have.
/// </summary>
public sealed class ConsoleCardRequest {
    public string CardId = "";
    public float? BearingDeg = null;
    public float? DistanceKm = null;
    public string? StartGrid = null;
    public int Priority = 50;
}

/// <summary>
/// Owns serialization for the three physically distinct shared operator consoles.
/// Per-gun reload/elevation work and the shared turret lane live in other modules.
/// </summary>
internal sealed class SharedConsoleCoordinator {
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    /// <summary>Background replenishment always gives way to task-driven and external console work.</summary>
    private const int PowderReplenishLockPriority = 20;

    private readonly FSC _fcs;
    private readonly List<ConsoleCardRequest> _cardRequests = new();
    private bool _draining;

    public CoroutineLock Ballistic { get; } = new();
    public CoroutineLock Requisition { get; } = new();
    public CoroutineLock Trigger { get; } = new();

    /// <summary>
    /// Result of the most recently completed card request, "" until the first one finishes.
    /// External readers poll this and treat an empty string as "no result yet"; the embedded
    /// timestamp is load bearing, it is what makes two identical outcomes distinguishable.
    /// </summary>
    public string LastCardRequestResult { get; private set; } = "";

    public SharedConsoleCoordinator(FSC fcs) {
        _fcs = fcs;
    }

    public void Reset() {
        Ballistic.Reset();
        Requisition.Reset();
        Trigger.Reset();
        _cardRequests.Clear();
        _draining = false;
    }

    /// <summary>
    /// Queues an external card purchase and kicks the drain coroutine if it is not already running.
    /// Event driven: no standing poll, no enqueue latency.
    /// </summary>
    public void EnqueueCardRequest(ConsoleCardRequest request) {
        _cardRequests.Add(request);
        if (!_draining)
            _fcs.TrackCoroutine(DrainCardRequests());
    }

    /// <summary>
    /// Removes and returns the highest priority pending request, or null when the list is empty.
    /// Strictly greater comparison keeps same-priority requests FIFO.
    /// </summary>
    private ConsoleCardRequest? PopHighestPriorityRequest() {
        if (_cardRequests.Count == 0)
            return null;

        var bestIndex = 0;
        for (var i = 1; i < _cardRequests.Count; i++) {
            if (_cardRequests[i].Priority > _cardRequests[bestIndex].Priority)
                bestIndex = i;
        }

        var request = _cardRequests[bestIndex];
        _cardRequests.RemoveAt(bestIndex);
        return request;
    }

    /// <summary>
    /// Serially executes queued card purchases. Each round pops one request up front; that request is
    /// then no longer preemptable - a higher priority request arriving while we wait for focus or for
    /// the requisition lock jumps ahead only on the next round.
    /// </summary>
    private IEnumerator DrainCardRequests() {
        // Must be set before the first yield. MelonCoroutines runs the leading segment synchronously,
        // so two EnqueueCardRequest calls in the same frame start exactly one drain; setting it after a
        // yield would let two drains run concurrently and both grab the requisition console.
        _draining = true;
        try {
            while (PopHighestPriorityRequest() is { } request) {
                yield return FcsRuntimeClock.WaitUntilFocused();

                MelonLogger.Msg(
                    $"[FCS] console card request: {request.CardId} P{request.Priority}"
                    + (request.BearingDeg is { } bearing ? $" bearing {bearing:F1}deg" : "")
                    + (request.DistanceKm is { } distance ? $" dist {distance:F1}km" : ""));

                yield return Requisition.Acquire(request.Priority);
                try {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    yield return _fcs.PurchaseDeck.BuyCardById(
                        request.CardId,
                        request.BearingDeg,
                        request.DistanceKm,
                        request.StartGrid,
                        result => {
                            LastCardRequestResult = $"{request.CardId}: {result} @{FcsRuntimeClock.Now:F0}";
                            MelonLogger.Msg($"[FCS] console card request {request.CardId} -> {result}");
                        });
                }
                finally {
                    Requisition.Release();
                }
            }
        }
        finally {
            // Also runs when the coroutine is stopped on unbind/F9, so the next bind can kick a drain.
            _draining = false;
        }
    }

    /// <summary>
    /// F9 abandons the old task but the physical review switches/arming levers survive in the game scene.
    /// Reset those controls immediately after bind so the next firing solution starts from a known baseline.
    /// </summary>
    public IEnumerator ResetFireControlsAfterBind() {
        yield return FcsRuntimeClock.WaitUntilFocused();
        yield return Trigger.Acquire();
        try {
            yield return _fcs.TriggerConsole.PrepareForNewFireSolution(LeftRight.Left);
        }
        finally {
            Trigger.Release();
        }
    }

    public IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return FcsRuntimeClock.WaitForSeconds(PowderCheckInterval);
            yield return FcsRuntimeClock.WaitUntilFocused();

            var charges = Math.Min(_fcs.LeftGun.RemainingCharges(), _fcs.RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;

            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return Requisition.Acquire(PowderReplenishLockPriority);
            try {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.PurchaseDeck.BuyPowders();
            }
            finally {
                Requisition.Release();
            }
        }
    }
}
