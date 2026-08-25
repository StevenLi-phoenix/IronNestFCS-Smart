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
/// Owns task queue/history and serial admission into planning rounds. Each round first builds a side-effect-free
/// eligibility matrix, then materializes only the Task x Gun edges selected by TaskGunMatcher before admission.
/// </summary>
internal sealed class TaskDispatcher
{
    private const int RecentTaskLimit = 20;
    private const float PhysicalRetryPollSeconds = 0.25f;
    private const float MatchCoalesceWindowSeconds = 1.0f;
    private const float MatchCoalescePollSeconds = 0.05f;
    private const int LeftPhysicalRetryBit = 1;
    private const int RightPhysicalRetryBit = 2;
    private const int UrgentPriorityThreshold = 90;

    private readonly FSC _fcs;
    private readonly Queue<ArtilleryTask> _taskQueue = new();
    private readonly Queue<ArtilleryTask> _recentTasks = new();

    private bool _planning;
    private bool _dispatchRequested;
    private bool _physicalRetryWaiting;
    private int _physicalRetryMask;

    // Unique task serial source (#1, #2, …). A task keeps its serial across re-enqueues
    // (preemption returns); the counter restarts with the dispatcher (F9 / scene reload).
    private int _serialCounter;

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
    }

    public void EnqueueTask(ArtilleryTask task)
    {
        if (task.serial == 0)
            task.serial = ++_serialCounter;
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

        // Urgent task with both guns busy: try to hijack a matching-load gun before dispatching.
        // The preempted task re-enters this queue via EnqueueTask (its priority < urgent, so no recursion).
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
    /// One match round coalesces a manually adjacent second task when both gun slots are free, captures one
    /// gun/loading snapshot, computes hard eligibility without game-console side effects, then materializes only
    /// the selected assignments. No FirePlan is admitted until every assignment in the chosen set materializes.
    /// </summary>
    private IEnumerator PlanPendingTasks()
    {
        yield return WaitForMatchCoalesceWindow();

        // Any enqueue observed during the coalescing window is now represented in _taskQueue and will be scanned
        // below. Clear only that consumed edge; a later enqueue during materialization will set it again.
        _dispatchRequested = false;

        // Late-bound solutions: re-derive every pending task's angel/distance from its
        // fixed aim point and the turret piece's current position before matching.
        foreach (var pending in _taskQueue)
        {
            // Moving targets: refit the motion model from the live entity (if tracked),
            // extrapolate the aim to predicted impact time, THEN re-derive the solution.
            if (pending.trackEntityId.Length > 0)
                _fcs.MapTable.UpdateEntityMotion(pending);
            _fcs.MapTable.ApplyMotionModel(pending);
            _fcs.MapTable.RefreshSolution(pending);
        }

        var snapshot = _fcs.Planner.CaptureSnapshot();
        PlanEngagementOrder(snapshot);
        var planningResults = new List<TaskPlanningResult>();
        // Retry ownership belongs to a free transient gun side, not to whether a particular task had zero
        // candidates. A task may be eligible on Left while Right is still recovering; if Right opens during
        // materialization, pending work must be dispatched into that newly free side.
        var deferredPhysicalMask = SnapshotTransientFreeSideMask(snapshot);
        var admittedAny = false;

        // The eligibility scan is fully synchronous (no yields), so the queue cannot change
        // under it — one snapshot copy is all the isolation it needs.
        foreach (var task in _taskQueue.ToArray())
        {
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

            result.Task.progress = Progress.Pending;
            result.Task.failureReason = "";
            if (result.HasCandidate)
                result.Task.pendingHint = PendingHint.None;
        }

        _planning = false;

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

    /// <summary>
    /// 炮击顺序规划 (engagement sequencing). Between consecutive shots the turret slews
    /// azimuth and cranks elevation IN PARALLEL, so the per-transition lay time is
    /// max(Δbearing/ωH, |Δelevation|/ωV) — a 2D sequence optimization under the
    /// Chebyshev-style metric (ωH=4°/s, ωV=2°/s from FireReadyEstimator). Priority
    /// bands are a hard outer order; within a band the path is solved EXACTLY by
    /// Held-Karp DP (bands are small; greedy fallback beyond ExactSequenceLimit).
    /// Elevation is not solved yet at queue time, so it is estimated from the verified
    /// linear ballistic model (distance*12/charge, min-viable or max charge).
    /// The queue itself is rebuilt in the planned order, so the HUD, the agent snapshot
    /// and matcher tie-breaking all follow one sequence. Runs every planning round on
    /// fresh solutions — recalibration, re-aims and cancellations re-plan automatically.
    /// </summary>
    private const int ExactSequenceLimit = 10;

    private void PlanEngagementOrder(FirePlanningSnapshot snapshot)
    {
        if (_taskQueue.Count < 2)
            return;

        var before = _taskQueue.Select(t => t.serial).ToArray();
        var ordered = new List<ArtilleryTask>(_taskQueue.Count);
        // Cursor in bearing space: the physical turret angle maps to bearing as its
        // negation (same convention as FireReadyEstimator.AzimuthSeconds).
        var cursorBearing = -snapshot.CurrentAzimuth;
        var cursorElevation = StartElevation(snapshot);
        var totalSeconds = 0f;

        foreach (var band in _taskQueue.GroupBy(t => t.priority).OrderByDescending(g => g.Key))
        {
            var tasks = band.OrderBy(t => t.serial).ToArray();
            var elevations = new float[tasks.Length];
            for (var i = 0; i < tasks.Length; i++)
                elevations[i] = EstimateElevation(tasks[i]);

            var path = tasks.Length <= ExactSequenceLimit
                ? SolveExactPath(tasks, elevations, cursorBearing, cursorElevation, out var pathSeconds)
                : SolveGreedyPath(tasks, elevations, cursorBearing, cursorElevation, out pathSeconds);
            totalSeconds += pathSeconds;

            foreach (var index in path)
                ordered.Add(tasks[index]);
            var last = path[^1];
            cursorBearing = tasks[last].angel;
            cursorElevation = elevations[last];
        }

        if (ordered.Select(t => t.serial).SequenceEqual(before))
            return;

        _taskQueue.Clear();
        foreach (var task in ordered)
            _taskQueue.Enqueue(task);
        MelonLogger.Msg($"[FCS Order] engagement sequence (est lay {totalSeconds:F0}s): " +
            string.Join(" -> ", ordered.Select(t => $"#{t.serial}(P{t.priority} {t.angel:F0}deg)")));
    }

    /// <summary>Per-transition lay time: both axes travel in parallel, the slower gates.</summary>
    private static float TransitionSeconds(float fromBearing, float fromElevation, float toBearing, float toElevation)
    {
        return Mathf.Max(
            Mathf.Abs(Mathf.DeltaAngle(fromBearing, toBearing)) / FireReadyEstimator.AzimuthSlewDegreesPerSecond,
            Mathf.Abs(fromElevation - toElevation) / FireReadyEstimator.ElevationSlewDegreesPerSecond);
    }

    /// <summary>Queue-time elevation estimate from the verified linear model (task charge is unsolved yet).</summary>
    private float EstimateElevation(ArtilleryTask task)
    {
        var charge = _fcs.MaxChargeEnabled ? 6 : BallisticCalculator.MinimumCharge(task.distance);
        if (charge <= 0)
            charge = 6;
        return Mathf.Min(task.distance * 12f / charge, 60f);
    }

    private static float StartElevation(FirePlanningSnapshot snapshot)
    {
        // The sequencer does not know gun assignment; start from the free gun's barrel,
        // or the average when both/neither slot is free.
        if (snapshot.LeftSlotAvailable && !snapshot.RightSlotAvailable)
            return snapshot.LeftPhysical.Elevation;
        if (snapshot.RightSlotAvailable && !snapshot.LeftSlotAvailable)
            return snapshot.RightPhysical.Elevation;
        return (snapshot.LeftPhysical.Elevation + snapshot.RightPhysical.Elevation) * 0.5f;
    }

    /// <summary>Held-Karp open-path DP: exact minimum total lay time for one priority band.</summary>
    private static int[] SolveExactPath(
        ArtilleryTask[] tasks, float[] elevations, float startBearing, float startElevation, out float bestSeconds)
    {
        var n = tasks.Length;
        var cost = new float[n + 1, n]; // row n = from the start cursor
        for (var j = 0; j < n; j++)
            cost[n, j] = TransitionSeconds(startBearing, startElevation, tasks[j].angel, elevations[j]);
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                cost[i, j] = TransitionSeconds(tasks[i].angel, elevations[i], tasks[j].angel, elevations[j]);

        var full = 1 << n;
        var dp = new float[full, n];
        var parent = new int[full, n];
        for (var mask = 0; mask < full; mask++)
            for (var j = 0; j < n; j++)
                dp[mask, j] = float.PositiveInfinity;
        for (var j = 0; j < n; j++)
            dp[1 << j, j] = cost[n, j];

        for (var mask = 1; mask < full; mask++)
            for (var last = 0; last < n; last++)
            {
                if ((mask & (1 << last)) == 0 || float.IsPositiveInfinity(dp[mask, last]))
                    continue;
                for (var next = 0; next < n; next++)
                {
                    if ((mask & (1 << next)) != 0)
                        continue;
                    var candidate = dp[mask, last] + cost[last, next];
                    var nextMask = mask | (1 << next);
                    if (candidate < dp[nextMask, next])
                    {
                        dp[nextMask, next] = candidate;
                        parent[nextMask, next] = last;
                    }
                }
            }

        var bestLast = 0;
        bestSeconds = float.PositiveInfinity;
        for (var j = 0; j < n; j++)
            if (dp[full - 1, j] < bestSeconds)
            {
                bestSeconds = dp[full - 1, j];
                bestLast = j;
            }

        var path = new int[n];
        var m = full - 1;
        var cursor = bestLast;
        for (var k = n - 1; k >= 0; k--)
        {
            path[k] = cursor;
            var prev = parent[m, cursor];
            m &= ~(1 << cursor);
            cursor = prev;
        }
        return path;
    }

    /// <summary>Same metric, nearest-next greedy — fallback for bands too large for exact DP.</summary>
    private static int[] SolveGreedyPath(
        ArtilleryTask[] tasks, float[] elevations, float startBearing, float startElevation, out float totalSeconds)
    {
        var n = tasks.Length;
        var path = new int[n];
        var used = new bool[n];
        var bearing = startBearing;
        var elevation = startElevation;
        totalSeconds = 0f;

        for (var k = 0; k < n; k++)
        {
            var best = -1;
            var bestCost = float.PositiveInfinity;
            for (var j = 0; j < n; j++)
            {
                if (used[j])
                    continue;
                var c = TransitionSeconds(bearing, elevation, tasks[j].angel, elevations[j]);
                if (c < bestCost)
                {
                    best = j;
                    bestCost = c;
                }
            }
            used[best] = true;
            path[k] = best;
            totalSeconds += bestCost;
            bearing = tasks[best].angel;
            elevation = elevations[best];
        }
        return path;
    }

    private IEnumerator WaitForMatchCoalesceWindow()
    {
        // Urgent tasks (counter-battery) never wait for a pairing partner.
        if (_taskQueue.Any(t => t.priority >= UrgentPriorityThreshold)
            || _taskQueue.Count != 1
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

    private static int SnapshotTransientFreeSideMask(FirePlanningSnapshot snapshot)
    {
        var mask = 0;
        if (snapshot.LeftSlotAvailable && IsTransient(snapshot.LeftLoading.PhysicalState))
            mask |= LeftPhysicalRetryBit;
        if (snapshot.RightSlotAvailable && IsTransient(snapshot.RightLoading.PhysicalState))
            mask |= RightPhysicalRetryBit;
        return mask;
    }

    // Table so every per-side rule is written once; the retry bitmask indexes into this.
    private static readonly (int Bit, LeftRight Side, GunSide Gun)[] RetrySides =
    {
        (LeftPhysicalRetryBit, LeftRight.Left, GunSide.Left),
        (RightPhysicalRetryBit, LeftRight.Right, GunSide.Right),
    };

    private int CurrentPlannableFreeSideMask(int sideMask)
    {
        var mask = 0;
        foreach (var (bit, side, gun) in RetrySides)
        {
            if ((sideMask & bit) != 0
                && _fcs.PlanExecutor.GetPlan(side) == null
                && IsPlannable(_fcs.Loading.GetSnapshot(gun).PhysicalState))
            {
                mask |= bit;
            }
        }
        return mask;
    }

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

                foreach (var (bit, side, gun) in RetrySides)
                {
                    if ((_physicalRetryMask & bit) == 0)
                        continue;
                    if (_fcs.PlanExecutor.GetPlan(side) != null)
                        _physicalRetryMask &= ~bit; // side got a plan; its completion re-triggers dispatch by event
                    else if (IsPlannable(_fcs.Loading.GetSnapshot(gun).PhysicalState))
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


    /// <summary>
    /// Commander-initiated cancellation of a PENDING task by its unique serial (#N) — the
    /// unambiguous handle (targetId is the recycled marker id and repeats). Executing plans
    /// are untouched (preemption handles those). Not counted as a failure.
    /// </summary>
    public string? CancelPendingBySerial(int serial)
    {
        var match = _taskQueue.FirstOrDefault(t => t.serial == serial);
        if (match == null || !RemovePendingTask(match))
            return null;
        match.progress = Progress.Failed;
        match.failureReason = "cancelled by commander";
        MelonLogger.Msg($"[FCS Dispatch] pending #{serial} cancelled by commander; pending={_taskQueue.Count}");
        return $"#{match.serial} {match.bulletType.DisplayName()} brg {match.angel:F1} dist {match.distance:F2}km";
    }

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
