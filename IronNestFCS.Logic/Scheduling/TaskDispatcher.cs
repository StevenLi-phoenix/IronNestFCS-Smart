// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Owns task queue/history and serial admission into planning rounds. Each round first refreshes the firing
/// solution of every pending task, plans the engagement order, then builds a side-effect-free eligibility matrix
/// and materializes only the Task x Gun edges selected by TaskGunMatcher before admission.
/// </summary>
internal sealed class TaskDispatcher
{
    private const int RecentTaskLimit = 20;
    private const float PhysicalRetryPollSeconds = 0.25f;
    private const float MatchCoalesceWindowSeconds = 1.0f;
    private const float MatchCoalescePollSeconds = 0.05f;
    private const int LeftPhysicalRetryBit = 1;
    private const int RightPhysicalRetryBit = 2;

    // Priority at or above which a task is urgent: it never waits for a partner to fill a two-gun batch
    // and may preempt a lower-priority plan that has not yet taken the shared bearing.
    public const int UrgentPriorityThreshold = 90;

    // Expiry is polled from Update, so it needs its own throttle; a planning round checks in-line as well.
    private const float ExpirySweepIntervalSeconds = 1f;

    // Held-Karp is exponential; above this band size the nearest-neighbour heuristic takes over.
    private const int ExactSequenceTaskLimit = 10;

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private bool _planning;
    private bool _dispatchRequested;
    private bool _physicalRetryWaiting;
    private int _physicalRetryMask;
    private int _serialCounter;
    private float _nextExpirySweepAt;

    public int PendingCount => _taskQueue.Count;

    // FirePlanExecutor uses this to decide whether a lone plan should wait for a possible partner.
    // Only an active planning round can still produce that partner; deferred pending tasks alone cannot.
    public bool HasPendingOrPlanning => _planning;

    public Queue<ArtilleryTask> QueueSnapshot => new(_taskQueue);
    public Queue<ArtilleryTask> RecentSnapshot => new(_recentTasks);

    public int CompletedTaskCount { get; private set; }
    public int SuccessfulTaskCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public TaskDispatcher(FSC fcs)
    {
        _fcs = fcs;
    }

    public void DisposeState()
    {
        _taskQueue.Clear();
        _recentTasks.Clear();
        _planning = false;
        _dispatchRequested = false;
        _physicalRetryWaiting = false;
        _physicalRetryMask = 0;
        // Serial numbering lives exactly as long as the dispatcher: F9 / scene change starts at #1 again.
        _serialCounter = 0;
        _nextExpirySweepAt = 0f;
    }

    public void EnqueueTask(ArtilleryTask task)
    {
        // Zero-value sentinels, not a private "was queued before" flag: a non-zero serial is always kept as-is,
        // including one an external bridge pre-set, and a requeue (urgent preemption, load recovery) keeps both
        // the number and the original command instant the validity window is measured from.
        if (task.serial == 0)
            task.serial = ++_serialCounter;
        if (task.firstEnqueuedAt == 0f)
            task.firstEnqueuedAt = FcsRuntimeClock.Now;

        // The whole baseline reset block is load-bearing on the requeue paths: chargeCount = 0 drops the
        // committed powder (otherwise AdjustAim, the planner's charge match and the preemption candidate filter
        // all read a stale charge) and elevation = 0f is the precondition of the executor's pre-aim fallback.
        task.progress = Progress.Pending;
        task.pendingHint = PendingHint.None;
        task.startedAt = FcsRuntimeClock.Now;
        task.completedAt = 0f;
        task.failureReason = "";
        task.chargeCount = 0;
        task.elevation = 0f;
        task.dispatchExcludedGunMask = 0;

        // Intent-only queue: no gun/loading read here.
        _taskQueue.Enqueue(task);
        MelonLogger.Msg($"[FCS Dispatch] queued #{task.serial} P{task.priority}; pending={_taskQueue.Count}");

        // Preemption is attempted only after the urgent task is already queued, so the victim that the nested
        // EnqueueTask puts back lands behind it. The short-circuit && is intentional: a failed attempt drops its
        // detail without logging anything.
        if (task.priority >= UrgentPriorityThreshold && !_fcs.PlanExecutor.HasFreeGun
            && _fcs.PlanExecutor.TryPreemptForUrgent(task, out var preemptDetail))
        {
            MelonLogger.Msg($"[FCS Dispatch] urgent #{task.serial}: {preemptDetail}");
        }

        TryDispatch();
    }

    public void TryDispatch()
    {
        // Planning is serialized, but a trigger that arrives while a round is running must not be lost.
        if (_planning)
        {
            _dispatchRequested = true;
            return;
        }

        if (!FcsRuntimeClock.IsFocused
            || _taskQueue.Count == 0
            || !_fcs.PlanExecutor.HasFreeGun)
            return;

        _dispatchRequested = false;
        _planning = true;
        _fcs.TrackCoroutine(PlanPendingTasks());
    }

    /// <summary>
    /// One match round coalesces a manually adjacent second task when both gun slots are free, refreshes every
    /// pending firing solution, plans the engagement order, captures one gun/loading snapshot, computes hard
    /// eligibility without game-console side effects, then materializes only the selected assignments. No FirePlan
    /// is admitted until every assignment in the chosen set materializes.
    /// </summary>
    private IEnumerator PlanPendingTasks()
    {
        yield return WaitForMatchCoalesceWindow();

        // Any enqueue observed during the coalescing window is now represented in _taskQueue and will be scanned
        // below. Clear only that consumed edge; a later enqueue during materialization will set it again.
        _dispatchRequested = false;

        RefreshPendingSolutions();

        var snapshot = _fcs.Planner.CaptureSnapshot();

        // Ordering runs on the snapshot but before the eligibility scan, so this round's planningResults order -
        // and with it the matcher's tie-breaks and the admission order - already follows the new sequence.
        PlanEngagementOrder(snapshot);

        var planningResults = new List<TaskPlanningResult>();
        // Retry ownership belongs to a free transient gun side, not to whether a particular task had zero
        // candidates. A task may be eligible on Left while Right is still recovering; if Right opens during
        // materialization, pending work must be dispatched into that newly free side.
        var deferredPhysicalMask = SnapshotTransientFreeSideMask(snapshot);
        var admittedAny = false;

        // Scan a copy: the first step of the scan can expire a task, which removes it from _taskQueue while the
        // scan is still walking it. Iterating the live queue would throw InvalidOperationException instead.
        foreach (var task in _taskQueue.ToArray())
        {
            if (TryExpireTask(task))
                continue;

            var result = _fcs.Planner.BuildEligibility(task, snapshot);
            planningResults.Add(result);

            if (!result.HasCandidate)
            {
                task.pendingHint = result.PendingHint;
                task.progress = Progress.Pending;
                task.failureReason = "";

                MelonLogger.Msg(
                    $"[FCS Dispatch] #{task.serial} remains pending; {result.FailureDetail}");
            }
        }

        var matchAt = FcsRuntimeClock.Now;
        foreach (var result in planningResults)
            result.FinalizeTiming(snapshot.SnapshotAt, matchAt);

        LogEligibilityMatrix(planningResults);

        // Materialization can reveal a ballistic/elevation failure that cannot be known without invoking the game
        // calculator. Exclude only that edge and rematch. Successful materializations are cached so a fallback
        // rematch never creates a second sticker for the same Task x Gun edge.
        var excludedEdges = new HashSet<(ArtilleryTask Task, LeftRight Side)>();
        var materializationCache = new Dictionary<(ArtilleryTask Task, LeftRight Side), FirePlanCandidate>();
        var materialized = new List<MaterializedAssignment>();

        while (true)
        {
            var assignments = TaskGunMatcher.Match(planningResults, excludedEdges);
            if (assignments.Count == 0)
                break;

            MelonLogger.Msg(
                $"[FCS Match] selected {assignments.Count} assignment(s): " +
                string.Join(", ", assignments.Select(DescribeAssignment)));

            materialized.Clear();
            var rematchRequired = false;

            foreach (var assignment in assignments)
            {
                var key = (assignment.Planning.Task, assignment.Candidate.Side);
                if (!materializationCache.TryGetValue(key, out var realized))
                {
                    FirePlanCandidate? resolved = null;
                    var failureReason = "";
                    yield return _fcs.Planner.MaterializeCandidate(
                        assignment.Planning.Task,
                        assignment.Candidate,
                        snapshot,
                        result => resolved = result,
                        reason => failureReason = reason);

                    if (resolved == null)
                    {
                        excludedEdges.Add(key);
                        rematchRequired = true;
                        MelonLogger.Warning(
                            $"[FCS Match] materialization rejected #{assignment.Planning.Task.serial}" +
                            $"->{assignment.Candidate.Side} {assignment.Candidate.Shell.DisplayName()} " +
                            $"C{assignment.Candidate.Charge}: {failureReason}; rematching remaining edges");
                        break;
                    }

                    realized = resolved;
                    materializationCache[key] = realized;
                }

                materialized.Add(new MaterializedAssignment(assignment, realized));
            }

            if (!rematchRequired)
                break;
        }

        var selectedTasks = new HashSet<ArtilleryTask>();

        if (materialized.Count > 0)
        {
            // Use one common commit time for all successfully materialized plans. Fresh loading/elevation cannot
            // consume time while the physical calculator is producing stickers; an already-running load can.
            var commitAt = FcsRuntimeClock.Now;
            foreach (var item in materialized)
                item.Candidate.FinalizeTiming(snapshot.SnapshotAt, commitAt);

            foreach (var item in materialized)
            {
                var assignment = item.Assignment;
                var task = assignment.Planning.Task;

                // PR review fix: MaterializeCandidate yields, and the whole match/materialize loop above can span
                // many frames. CancelPendingBySerial and TryExpireTask/SweepExpiredTasks both run in that window,
                // and both set Progress.Failed, dequeue the task and record its result in the same frame. Admitting
                // such a task now would revive an already-reported-dead serial onto a gun and fire it. Discard the
                // assignment instead: no gun slot is taken, so the side stays free for the retry round requested
                // below, and the task is deliberately left out of selectedTasks and out of the Pending reset.
                if (task.progress == Progress.Failed)
                {
                    MelonLogger.Warning(
                        $"[FCS Dispatch] #{task.serial} was cancelled/expired during materialization; discarding plan");

                    // The slot this assignment would have taken is still free. Reuse the coalesced-trigger edge so
                    // the remaining pending work gets a fresh round instead of waiting for the next external event.
                    if (_taskQueue.Count > 0)
                        _dispatchRequested = true;
                    continue;
                }

                var plan = _fcs.Planner.CreatePlan(assignment.Planning, item.Candidate, commitAt);

                if (!_fcs.PlanExecutor.AddPlan(plan, out var addReason))
                {
                    task.progress = Progress.Pending;
                    task.pendingHint = PendingHint.None;
                    task.failureReason = "";
                    MelonLogger.Warning(
                        $"[FCS Dispatch] #{task.serial} matched FirePlan was not admitted and remains pending: {addReason}");
                    continue;
                }

                // Take the gun slot first and leave the queue only after that succeeded: an external reader must
                // never observe a frame in which the task is neither pending nor on a gun.
                if (!RemovePendingTask(task))
                    MelonLogger.Warning($"[FCS Dispatch] admitted #{task.serial} was no longer present in pending queue");

                selectedTasks.Add(task);
                admittedAny = true;
                MelonLogger.Msg($"[FCS Dispatch] admitted #{task.serial}; pending={_taskQueue.Count}");
            }
        }

        // Every evaluated but unselected task remains pending. A valid pre-match candidate can lose because a
        // higher-quality complete match consumed the free slots, or because a selected edge failed materialization.
        foreach (var result in planningResults)
        {
            if (selectedTasks.Contains(result.Task))
                continue;

            // PR review fix: same race as the admission guard above. A task cancelled or expired during
            // materialization is already Failed, already out of the queue and already in RecentTasks; writing
            // Pending back over it would resurrect a queue-less "pending" task that no round can ever reach.
            if (result.Task.progress == Progress.Failed)
                continue;

            result.Task.progress = Progress.Pending;
            result.Task.failureReason = "";
            if (result.HasCandidate)
                result.Task.pendingHint = PendingHint.None;
        }

        _planning = false;

        // Counted from the planning results, so a task that expired in this round's scan is not reported as
        // deferred - it was never evaluated.
        if (!admittedAny && planningResults.Count > 0)
            MelonLogger.Msg($"[FCS Dispatch] planning round deferred {planningResults.Count} pending task(s)");

        // Plans finish at physical shot, so preserve event-driven dispatch for any free side that was transient
        // in this round's snapshot. If it became plannable while materializing another assignment, immediately
        // open the next planning round; otherwise keep one temporary waiter for the physical transition.
        if (deferredPhysicalMask != 0 && _taskQueue.Count > 0)
        {
            if (CurrentPlannableFreeSideMask(deferredPhysicalMask) != 0)
                _dispatchRequested = true;
            else
                EnsurePhysicalRetryWait(deferredPhysicalMask);
        }

        // Consume one coalesced trigger that arrived after the eligibility scan had already closed. TryDispatch()
        // sets _planning synchronously before the next coroutine starts, preserving scheduler pair waiting.
        if (_dispatchRequested)
        {
            _dispatchRequested = false;
            TryDispatch();
        }

        _fcs.PlanExecutor.EvaluateScheduling();
    }

    private IEnumerator WaitForMatchCoalesceWindow()
    {
        // An urgent task must not spend the coalescing window waiting for a partner to fill a two-gun batch.
        if (HasUrgentPending())
            yield break;

        if (_taskQueue.Count != 1
            || _fcs.PlanExecutor.GetPlan(LeftRight.Left) != null
            || _fcs.PlanExecutor.GetPlan(LeftRight.Right) != null)
        {
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + MatchCoalesceWindowSeconds;
        while (_taskQueue.Count < 2 && FcsRuntimeClock.Now < deadline)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (_taskQueue.Count >= 2)
                break;
            yield return FcsRuntimeClock.WaitForSeconds(MatchCoalescePollSeconds);
        }
    }

    private bool HasUrgentPending()
    {
        foreach (var task in _taskQueue)
        {
            if (task.priority >= UrgentPriorityThreshold)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Re-solve every pending task at the start of a planning round. Only entity tracking is conditional: a
    /// motion model pushed in from outside, and a purely static aim point that only needs the firing origin
    /// re-read, must be refreshed as well.
    /// </summary>
    private void RefreshPendingSolutions()
    {
        foreach (var pending in _taskQueue.ToArray())
        {
            if (pending.trackEntityId.Length > 0)
                _fcs.MapTable.UpdateEntityMotion(pending);
            _fcs.MapTable.ApplyMotionModel(pending);
            _fcs.MapTable.RefreshSolution(pending);
        }
    }

    /// <summary>
    /// Public sweep so time-critical tasks still expire while no planning round runs. Throttled to once per
    /// second because FSC.Update calls it every frame.
    /// </summary>
    public void SweepExpiredTasks()
    {
        var now = FcsRuntimeClock.Now;
        if (now < _nextExpirySweepAt)
            return;

        _nextExpirySweepAt = now + ExpirySweepIntervalSeconds;

        // Copy: TryExpireTask removes from _taskQueue while this loop walks it.
        foreach (var task in _taskQueue.ToArray())
            TryExpireTask(task);
    }

    /// <summary>
    /// Auto-cancel a still-queued time-critical task whose validity window has elapsed. A task that already
    /// reached a gun never expires - only the pending queue is swept.
    /// </summary>
    private bool TryExpireTask(ArtilleryTask task)
    {
        if (task.validForSeconds <= 0f)
            return false;

        if (FcsRuntimeClock.Now - task.firstEnqueuedAt <= task.validForSeconds)
            return false;

        // Leaving the queue is what makes this an expiry; if the task is no longer pending (already admitted or
        // already swept in this frame) there is nothing to cancel and nothing to record.
        if (!RemovePendingTask(task))
            return false;

        task.progress = Progress.Failed;
        // The configured window, not the measured dwell: the 1s sweep throttle makes the measured value drift
        // past validForSeconds and the difference is visible after :F0.
        task.failureReason = $"时效已过: 入队{task.validForSeconds:F0}秒仍未上炮, 时敏任务自动撤销";

        // Same frame as the removal: an external reader that sees the task leave the active set without a Failed
        // record in RecentTasks would report it as a shell in flight.
        RecordTaskResult(task);

        MelonLogger.Warning(
            $"[FCS Dispatch] #{task.serial} expired after {task.validForSeconds:F0}s in queue; auto-cancelled");
        return true;
    }

    /// <summary>
    /// Cancel one pending task by serial. Tasks already on a gun are not cancellable here - urgent preemption
    /// owns that case. Returns null when no pending task carries the serial; callers distinguish null strictly.
    /// </summary>
    public string? CancelPendingBySerial(int serial)
    {
        ArtilleryTask? match = null;
        foreach (var task in _taskQueue)
        {
            if (task.serial == serial)
            {
                match = task;
                break;
            }
        }

        if (match == null)
            return null;

        RemovePendingTask(match);
        match.progress = Progress.Failed;
        match.failureReason = "cancelled by commander";

        // A cancelled task must reach RecentTasks in the same frame it leaves the queue. Without the Failed
        // record an external reader treats the vanished serial as a shell that left the barrel and locks the
        // target for its whole flight window. The failure counters moving with it is the accepted consequence.
        RecordTaskResult(match);

        MelonLogger.Msg($"[FCS Dispatch] pending #{match.serial} cancelled by commander; pending={_taskQueue.Count}");

        return $"#{match.serial} {match.bulletType.DisplayName()} brg {match.angel:F1} dist {match.distance:F2}km";
    }

    /// <summary>
    /// Reorder the pending queue to minimise total turret travel while honouring priority strictly. Priority
    /// bands are hard: every distinct priority value is its own band and bands run in descending order; only the
    /// order inside a band is optimised. Transition cost is Chebyshev - azimuth and elevation slew in parallel.
    /// </summary>
    private void PlanEngagementOrder(FirePlanningSnapshot snapshot)
    {
        if (_taskQueue.Count < 2)
            return;

        // Physical turret angle and map bearing are negatives of each other.
        var cursorBearing = -snapshot.CurrentAzimuth;
        var cursorElevation = StartCursorElevation(snapshot);

        var ordered = new List<ArtilleryTask>(_taskQueue.Count);
        var totalSeconds = 0f;

        foreach (var band in _taskQueue.GroupBy(task => task.priority).OrderByDescending(group => group.Key))
        {
            // Deterministic input order so that equal-cost branches always resolve the same way.
            var candidates = band.OrderBy(task => task.serial).ToList();
            if (candidates.Count == 0)
                continue;

            var elevations = new float[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
                elevations[i] = EstimatedQueueElevation(candidates[i]);

            // Exact below the limit, nearest-neighbour above it; both use the same metric and the same tie rule.
            float pathSeconds;
            List<ArtilleryTask> sequence;
            if (candidates.Count <= ExactSequenceTaskLimit)
                sequence = SolveBandExact(candidates, elevations, cursorBearing, cursorElevation, out pathSeconds);
            else
                sequence = SolveBandGreedy(candidates, elevations, cursorBearing, cursorElevation, out pathSeconds);

            totalSeconds += pathSeconds;
            ordered.AddRange(sequence);

            // Roll the cursor onto the last task of this band, so the next band pays for the real handover.
            var last = sequence[sequence.Count - 1];
            cursorBearing = last.angel;
            cursorElevation = EstimatedQueueElevation(last);
        }

        if (ordered.Count != _taskQueue.Count)
            return;

        var current = _taskQueue.ToArray();
        var changed = false;
        for (var i = 0; i < current.Length; i++)
        {
            if (!ReferenceEquals(current[i], ordered[i]))
            {
                changed = true;
                break;
            }
        }

        // No reordering means no side effect at all - the queue object, the HUD and the log stay untouched.
        if (!changed)
            return;

        _taskQueue.Clear();
        foreach (var task in ordered)
            _taskQueue.Enqueue(task);

        MelonLogger.Msg(
            $"[FCS Order] engagement sequence (est lay {totalSeconds:F0}s): " +
            string.Join(" -> ", ordered.Select(task => $"#{task.serial}(P{task.priority} {task.angel:F0}deg)")));
    }

    /// <summary>
    /// Where the turret starts from for the first hop. Pure slot availability, reading the physical elevation:
    /// with exactly one slot free that gun's elevation is the cursor, otherwise both guns average out.
    /// </summary>
    private static float StartCursorElevation(FirePlanningSnapshot snapshot)
    {
        if (snapshot.LeftSlotAvailable && !snapshot.RightSlotAvailable)
            return snapshot.LeftPhysical.Elevation;
        if (snapshot.RightSlotAvailable && !snapshot.LeftSlotAvailable)
            return snapshot.RightPhysical.Elevation;
        return (snapshot.LeftPhysical.Elevation + snapshot.RightPhysical.Elevation) * 0.5f;
    }

    /// <summary>
    /// Queued tasks have no solved elevation yet, so estimate one from the linear ballistic model with the
    /// charge the planner would pick.
    /// </summary>
    private float EstimatedQueueElevation(ArtilleryTask task)
    {
        var charge = _fcs.MaxChargeEnabled ? 6 : BallisticCalculator.MinimumCharge(task.distance);
        if (charge <= 0)
            charge = 6;
        return Mathf.Min(task.distance * 12f / charge, 60f);
    }

    /// <summary>
    /// Both axes move in parallel, so a transition costs whichever axis is slower. Azimuth takes the short arc;
    /// elevation does not wrap.
    /// </summary>
    private static float TransitionSeconds(float fromBearing, float fromElevation, float toBearing, float toElevation)
    {
        return Mathf.Max(
            Mathf.Abs(Mathf.DeltaAngle(fromBearing, toBearing)) / FireReadyEstimator.AzimuthSlewDegreesPerSecond,
            Mathf.Abs(fromElevation - toElevation) / FireReadyEstimator.ElevationSlewDegreesPerSecond);
    }

    /// <summary>
    /// Exact open-path Held-Karp over one band. pathSeconds includes the hop from the incoming cursor to the
    /// first task; ties always keep the candidate reached first, i.e. the lower serial.
    /// </summary>
    private static List<ArtilleryTask> SolveBandExact(
        IReadOnlyList<ArtilleryTask> candidates,
        IReadOnlyList<float> elevations,
        float cursorBearing,
        float cursorElevation,
        out float pathSeconds)
    {
        var n = candidates.Count;
        var cost = new float[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                cost[i, j] = i == j
                    ? 0f
                    : TransitionSeconds(candidates[i].angel, elevations[i], candidates[j].angel, elevations[j]);
            }
        }

        var full = 1 << n;
        var dp = new float[full, n];
        var parent = new int[full, n];
        for (var mask = 0; mask < full; mask++)
        {
            for (var j = 0; j < n; j++)
            {
                dp[mask, j] = float.PositiveInfinity;
                parent[mask, j] = -1;
            }
        }

        for (var j = 0; j < n; j++)
        {
            dp[1 << j, j] = TransitionSeconds(
                cursorBearing, cursorElevation, candidates[j].angel, elevations[j]);
        }

        for (var mask = 1; mask < full; mask++)
        {
            for (var last = 0; last < n; last++)
            {
                if ((mask & (1 << last)) == 0)
                    continue;

                var reached = dp[mask, last];
                if (float.IsPositiveInfinity(reached))
                    continue;

                for (var next = 0; next < n; next++)
                {
                    if ((mask & (1 << next)) != 0)
                        continue;

                    var nextMask = mask | (1 << next);
                    var candidate = reached + cost[last, next];
                    if (candidate < dp[nextMask, next])
                    {
                        dp[nextMask, next] = candidate;
                        parent[nextMask, next] = last;
                    }
                }
            }
        }

        var bestLast = 0;
        var bestSeconds = float.PositiveInfinity;
        for (var j = 0; j < n; j++)
        {
            if (dp[full - 1, j] < bestSeconds)
            {
                bestSeconds = dp[full - 1, j];
                bestLast = j;
            }
        }

        var order = new List<ArtilleryTask>(n);
        var walkMask = full - 1;
        var node = bestLast;
        while (node >= 0)
        {
            order.Add(candidates[node]);
            var previous = parent[walkMask, node];
            walkMask &= ~(1 << node);
            node = previous;
        }

        order.Reverse();
        pathSeconds = bestSeconds;
        return order;
    }

    /// <summary>
    /// Nearest-neighbour fallback for bands too large for the exact solver, same metric and same tie rule.
    /// The first iteration pays the hop from the incoming cursor, exactly like the exact solver's DP seed.
    /// </summary>
    private static List<ArtilleryTask> SolveBandGreedy(
        IReadOnlyList<ArtilleryTask> candidates,
        IReadOnlyList<float> elevations,
        float cursorBearing,
        float cursorElevation,
        out float pathSeconds)
    {
        var n = candidates.Count;
        var visited = new bool[n];
        var order = new List<ArtilleryTask>(n);
        var bearing = cursorBearing;
        var elevation = cursorElevation;
        pathSeconds = 0f;

        for (var step = 0; step < n; step++)
        {
            var best = -1;
            var bestCost = float.PositiveInfinity;
            for (var j = 0; j < n; j++)
            {
                if (visited[j])
                    continue;

                var c = TransitionSeconds(bearing, elevation, candidates[j].angel, elevations[j]);
                if (c < bestCost)
                {
                    best = j;
                    bestCost = c;
                }
            }

            if (best < 0)
                break;

            visited[best] = true;
            order.Add(candidates[best]);
            pathSeconds += bestCost;
            bearing = candidates[best].angel;
            elevation = elevations[best];
        }

        return order;
    }

    private void LogEligibilityMatrix(IReadOnlyList<TaskPlanningResult> results)
    {
        foreach (var result in results)
        {
            var left = DescribeCandidate(result.LeftCandidate, result.LeftReason);
            var right = DescribeCandidate(result.RightCandidate, result.RightReason);
            MelonLogger.Msg($"[FCS Match] #{result.Task.serial}: Left={left}; Right={right}");
        }
    }

    private static string DescribeCandidate(TaskGunCandidate? candidate, string failureReason)
    {
        if (candidate != null)
        {
            var eta = candidate.EtaKnown
                ? Math.Max(0f, candidate.EstimatedReadyAt - FcsRuntimeClock.Now).ToString("F1") + "s"
                : "unknown";
            return $"eligible {candidate.Shell.DisplayName()} C{candidate.Charge} preETA={eta}";
        }

        return string.IsNullOrWhiteSpace(failureReason) ? "ineligible" : failureReason;
    }

    private static string DescribeAssignment(TaskGunAssignment assignment)
    {
        var task = assignment.Planning.Task;
        var candidate = assignment.Candidate;
        var minimumCharge = BallisticCalculator.MinimumCharge(task.distance);
        var chargeExcess = Math.Max(0, candidate.Charge - minimumCharge);
        return $"#{task.serial}->{candidate.Side} {candidate.Shell.DisplayName()} C{candidate.Charge} " +
               $"(chargeExcess={chargeExcess})";
    }

    // One table for both sides removes the left/right copy-paste from every retry decision below.
    private static readonly SideRetryRule[] SideRetryRules =
    {
        new(LeftPhysicalRetryBit, LeftRight.Left, GunSide.Left),
        new(RightPhysicalRetryBit, LeftRight.Right, GunSide.Right),
    };

    private static int SnapshotTransientFreeSideMask(FirePlanningSnapshot snapshot)
    {
        var mask = 0;
        foreach (var rule in SideRetryRules)
        {
            if (SlotAvailable(snapshot, rule.Side) && IsTransient(LoadingOf(snapshot, rule.Side).PhysicalState))
                mask |= rule.Bit;
        }

        return mask;
    }

    private int CurrentPlannableFreeSideMask(int sideMask)
    {
        var mask = 0;
        foreach (var rule in SideRetryRules)
        {
            if ((sideMask & rule.Bit) != 0
                && _fcs.PlanExecutor.GetPlan(rule.Side) == null
                && IsPlannable(_fcs.Loading.GetSnapshot(rule.Gun).PhysicalState))
            {
                mask |= rule.Bit;
            }
        }

        return mask;
    }

    private static bool SlotAvailable(FirePlanningSnapshot snapshot, LeftRight side) =>
        side == LeftRight.Left ? snapshot.LeftSlotAvailable : snapshot.RightSlotAvailable;

    private static LoadingSnapshot LoadingOf(FirePlanningSnapshot snapshot, LeftRight side) =>
        side == LeftRight.Left ? snapshot.LeftLoading : snapshot.RightLoading;

    private void EnsurePhysicalRetryWait(int sideMask)
    {
        _physicalRetryMask |= sideMask;
        if (_physicalRetryWaiting)
            return;

        _physicalRetryWaiting = true;
        _fcs.TrackCoroutine(WaitForPhysicalPlanningOpportunity());
    }

    private IEnumerator WaitForPhysicalPlanningOpportunity()
    {
        var shouldRetry = false;
        try
        {
            while (_taskQueue.Count > 0 && _physicalRetryMask != 0)
            {
                yield return FcsRuntimeClock.WaitUntilFocused();

                foreach (var rule in SideRetryRules)
                {
                    if ((_physicalRetryMask & rule.Bit) == 0)
                        continue;

                    if (_fcs.PlanExecutor.GetPlan(rule.Side) != null)
                    {
                        // Someone else took that side; stop waiting on it.
                        _physicalRetryMask &= ~rule.Bit;
                    }
                    else if (IsPlannable(_fcs.Loading.GetSnapshot(rule.Gun).PhysicalState))
                    {
                        shouldRetry = true;
                        break;
                    }
                }

                if (shouldRetry)
                    break;

                yield return FcsRuntimeClock.WaitForSeconds(PhysicalRetryPollSeconds);
            }
        }
        finally
        {
            _physicalRetryWaiting = false;
            _physicalRetryMask = 0;
        }

        if (shouldRetry && _taskQueue.Count > 0)
        {
            MelonLogger.Msg("[FCS Dispatch] physical recovery opened a planning opportunity; retrying pending tasks");
            TryDispatch();
        }
    }

    private static bool IsPlannable(LoadingPhysicalState state) =>
        state == LoadingPhysicalState.EmptyReady
        || state == LoadingPhysicalState.ShellLoaded
        || state == LoadingPhysicalState.LoadedReady;

    private static bool IsTransient(LoadingPhysicalState state) =>
        state == LoadingPhysicalState.Recovering
        || state == LoadingPhysicalState.PostShotRecovery
        || state == LoadingPhysicalState.Unknown
        || state == LoadingPhysicalState.Unbound;

    private bool RemovePendingTask(ArtilleryTask target)
    {
        var items = _taskQueue.ToArray();
        _taskQueue.Clear();

        var removed = false;
        foreach (var task in items)
        {
            if (!removed && ReferenceEquals(task, target))
            {
                removed = true;
                continue;
            }

            _taskQueue.Enqueue(task);
        }

        return removed;
    }

    public void RecordTaskResult(ArtilleryTask task)
    {
        task.completedAt = FcsRuntimeClock.Now;
        CompletedTaskCount++;
        if (task.progress == Progress.Finished)
            SuccessfulTaskCount++;
        else if (task.progress == Progress.Failed)
            FailedTaskCount++;

        _recentTasks.Enqueue(task);
        while (_recentTasks.Count > RecentTaskLimit)
            _recentTasks.Dequeue();
        _fcs.SceneInteractor.TaskFinished(task);
    }

    private readonly struct SideRetryRule
    {
        public int Bit { get; }
        public LeftRight Side { get; }
        public GunSide Gun { get; }

        public SideRetryRule(int bit, LeftRight side, GunSide gun)
        {
            Bit = bit;
            Side = side;
            Gun = gun;
        }
    }

    private sealed class MaterializedAssignment
    {
        public TaskGunAssignment Assignment { get; }
        public FirePlanCandidate Candidate { get; }

        public MaterializedAssignment(TaskGunAssignment assignment, FirePlanCandidate candidate)
        {
            Assignment = assignment;
            Candidate = candidate;
        }
    }
}
