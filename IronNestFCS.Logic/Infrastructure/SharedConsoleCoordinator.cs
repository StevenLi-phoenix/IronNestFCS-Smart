using System.Collections;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic.Infrastructure;

/// <summary>External punchcard purchase request (e.g. scout plane), executed by the coordinator.</summary>
public sealed class ConsoleCardRequest {
    public string CardId = "";
    public float? BearingDeg;
    /// <summary>Recon start cell like "P4" — drives the console's grid split-flap dials.</summary>
    public string? StartGrid;
    /// <summary>Higher runs first (e.g. 紧急转移 emergency-relocation cards at 100).</summary>
    public int Priority = 50;
}

/// <summary>
/// Owns serialization for the three physically distinct shared operator consoles.
/// Per-gun reload/elevation work and the shared turret lane live in other modules.
/// </summary>
internal sealed class SharedConsoleCoordinator {
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    private readonly FSC _fcs;

    public CoroutineLock Ballistic { get; } = new();
    public CoroutineLock Requisition { get; } = new();
    public CoroutineLock Trigger { get; } = new();

    public SharedConsoleCoordinator(FSC fcs) {
        _fcs = fcs;
    }

    public void Reset() {
        Ballistic.Reset();
        Requisition.Reset();
        Trigger.Reset();
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

    private readonly List<ConsoleCardRequest> _cardRequests = new();

    /// <summary>Latest completed card-request outcome, for external observers to poll.</summary>
    public string LastCardRequestResult { get; private set; } = "";

    public void EnqueueCardRequest(ConsoleCardRequest request) => _cardRequests.Add(request);

    private ConsoleCardRequest? PopHighestPriorityRequest() {
        if (_cardRequests.Count == 0) return null;
        ConsoleCardRequest? best = null;
        foreach (var request in _cardRequests) {
            if (best == null || request.Priority > best.Priority)
                best = request; // list order is FIFO within equal priority
        }
        _cardRequests.Remove(best!);
        return best;
    }

    /// <summary>
    /// Drains externally submitted punchcard purchases inside the coordinator's own
    /// Requisition-lock discipline — no external mod ever touches the console directly.
    /// </summary>
    public IEnumerator ConsoleCardRequestLoop() {
        while (true) {
            yield return FcsRuntimeClock.WaitForSeconds(1f);
            if (_cardRequests.Count == 0) continue;
            yield return FcsRuntimeClock.WaitUntilFocused();

            var request = PopHighestPriorityRequest();
            if (request == null) continue;
            MelonLogger.Msg(
                $"[FCS] console card request: {request.CardId} P{request.Priority}" +
                (request.BearingDeg is { } b ? $" bearing {b:F1}掳" : ""));

            yield return Requisition.Acquire(request.Priority);
            try {
                yield return FcsRuntimeClock.WaitUntilFocused();
                yield return _fcs.PurchaseDeck.BuyCardById(request.CardId, request.BearingDeg, request.StartGrid, result => {
                    LastCardRequestResult = $"{request.CardId}: {result} @{FcsRuntimeClock.Now:F0}";
                    MelonLogger.Msg($"[FCS] console card request {request.CardId} -> {result}");
                });
            }
            finally {
                Requisition.Release();
            }
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
            // Background top-up: always yields to task loading and card requests.
            yield return Requisition.Acquire(20);
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
