// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Text.RegularExpressions;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Scheduling;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Execution;

/// <summary>
/// Capacity-two FirePlan executor. Per-gun preparation runs independently; current/next determine the automation
/// order and arming suggestion, while actual physical gun state determines which FirePlan was really executed.
/// A FirePlan ends at observed fire; post-shot mechanical recovery remains owned by the physical loading runtime.
/// </summary>
internal sealed class FirePlanExecutor
{
    private const float ElevationTimeoutSeconds = 35f;
    private const float LoadingObservationTimeoutSeconds = 90f;
    private const float AutoFireTimeoutSeconds = 25f;
    private const float SameAzimuthToleranceDegrees = 0.09f;
    private const int FireSettlementBufferFrames = 3;
    private const float ReviewLeadTimeBeforeArmSeconds = 1.5f;

    // Execution-stage tracking correction. Loading and a large elevation slew take minutes, so most of the
    // aim drift on a moving target accumulates here. Corrections are deliberately late and small.
    private const float TrackRelayIntervalSeconds = 3f;
    private const float TrackAzimuthEpsilonDegrees = 0.1f;
    private const float TrackDistanceEpsilonKm = 0.03f;
    private const float TrackElevationEpsilonDegrees = 0.05f;
    private const float PreAimPrepSeconds = 45f;
    private const float PreFirePrepSeconds = 15f;

    // About one third of the HE lethal radius. Below that a correction costs more turret time than it buys.
    private const float PreFireSignificantErrorKm = 0.05f;

    // A re-lay during a human fire wait must never delay a new task's planning solve, so it takes the
    // ballistic desk at the lowest priority in use (appendix B).
    private const int ManualRelayLockPriority = 10;

    // Powder-failure recovery bounds. Both recovery paths share ArtilleryTask.loadRetryCount.
    private const int PowderCommitRetryLimit = 3;
    private const int PowderDispenserRetryLimit = 2;
    private const float ChamberClearingRangeFactor = 0.9f;

    // Produced verbatim by the loading runtime; the two captured groups are the expected and physical charge.
    private static readonly Regex PowderCommitMismatchPattern =
        new(@"powder commit mismatch: expected C(\d+), physical C(\d+)");

    private readonly FSC _fcs;
    private readonly Dictionary<FirePlan, object> _prepareCoroutines = new();

    private FirePlan? _leftPlan;
    private FirePlan? _rightPlan;
    private FirePlan? _current;
    private FirePlan? _next;

    // This identifies one shared physical-fire wait, not the gun that must fire. Left/right result mapping is
    // performed from physical observations after the shared trigger event.
    private FirePlan? _fireWaitOwner;
    private int _fireWaitGeneration = -1;
    private int _fireWaitSerial;
    private int _activeFireWaitSerial;
    private bool _autoFireIssuedForWait;

    public FirePlanExecutor(FSC fcs)
    {
        _fcs = fcs;
    }

    public ArtilleryTask? LeftTask => _leftPlan?.Task;
    public ArtilleryTask? RightTask => _rightPlan?.Task;

    // "Free" means a FirePlan slot is free. FirePlanner remains authoritative for whether the underlying gun's
    // current loading/recovery snapshot is physically plannable.
    public bool HasFreeGun => _leftPlan == null || _rightPlan == null;

    public FirePlan? GetPlan(LeftRight side) => side == LeftRight.Left ? _leftPlan : _rightPlan;

    public void DisposeState()
    {
        _leftPlan = null;
        _rightPlan = null;
        _current = null;
        _next = null;
        _prepareCoroutines.Clear();
        _fcs.TriggerConsole.ResetGunReadyInputs();
        ClearAllFireWait();
    }

    public bool AddPlan(FirePlan plan, out string reason)
    {
        reason = "";
        if (plan.Generation != _fcs.FirePriority.Generation)
        {
            reason = "stale FirePlan generation";
            return false;
        }

        if (GetPlan(plan.Side) != null)
        {
            reason = $"{plan.Side} already has a FirePlan";
            return false;
        }

        if (plan.Side == LeftRight.Left)
            _leftPlan = plan;
        else
            _rightPlan = plan;

        if (_current != null && !ReferenceEquals(_current, plan) && _next == null)
            _next = plan;

        MelonLogger.Msg($"[FCS Plan] executor accepted {plan.Label}");
        _prepareCoroutines[plan] = _fcs.TrackCoroutine(PrepareLocal(plan));
        EvaluateScheduling();
        return true;
    }

    public void Tick() => EvaluateScheduling();

    /// <summary>
    /// Free a gun for an urgent task by rolling one prepared plan back into the pending queue. Only a plan whose
    /// physical work can be inherited is eligible: the already committed powder must reach the urgent target and
    /// the shell must match, because a committed charge can only leave the barrel by being fired.
    /// </summary>
    public bool TryPreemptForUrgent(ArtilleryTask urgent, out string detail)
    {
        if (HasFreeGun)
        {
            detail = "a gun is already free";
            return false;
        }

        var requiredCharge = BallisticCalculator.MinimumCharge(urgent.distance);
        FirePlan? victim = null;

        // Left before right, replacing only on a strictly lower priority, so equal priorities pick the left gun.
        foreach (var plan in new[] { _leftPlan, _rightPlan })
        {
            if (plan == null
                || plan.Task.priority >= urgent.priority
                || ReferenceEquals(_current, plan)
                || ReferenceEquals(_fireWaitOwner, plan)
                || plan.ShotObserved
                || plan.Shell != urgent.bulletType
                || plan.Charge < requiredCharge)
            {
                continue;
            }

            if (victim == null || plan.Task.priority < victim.Task.priority)
                victim = plan;
        }

        if (victim == null)
        {
            detail = "no preemptable plan (current/armed, higher priority, or shell/charge mismatch)";
            return false;
        }

        // Logged before any teardown so this line precedes both the victim's re-queue line and the caller's
        // urgent line, leaving the console in causal order.
        MelonLogger.Msg(
            $"[FCS Plan] {victim.Label} preempted by urgent #{urgent.serial} P{urgent.priority} " +
            $"(load {victim.Shell.DisplayName()} C{victim.Charge} transfers; min required C{requiredCharge})");

        CancelPreparation(victim);
        victim.Failed = true;
        victim.FailureReason = "preempted by urgent task";
        if (ReferenceEquals(_fireWaitOwner, victim))
            ClearAllFireWait();
        ReleaseGunSlot(victim, notify: false);

        // The plan died, the task did not. It keeps its serial and goes back to pending; its elevation will be
        // re-solved against whatever charge it eventually gets.
        var task = victim.Task;
        task.progress = Progress.Pending;
        task.failureReason = "";
        task.pendingHint = PendingHint.None;
        _fcs.Dispatcher.EnqueueTask(task);

        detail = $"preempted {victim.Label}";
        return true;
    }

    public void OnAutoFireEnabled()
    {
        var plan = _fireWaitOwner;
        if (plan == null || _autoFireIssuedForWait)
            return;

        if (_fireWaitGeneration != _fcs.FirePriority.Generation
            || !ReferenceEquals(_current, plan)
            || !IsActive(plan)
            || plan.Task.progress != Progress.WaitingForFire)
        {
            ClearAllFireWait();
            return;
        }

        _autoFireIssuedForWait = true;
        MelonLogger.Msg($"[FCS] AutoFire enabled while #{plan.Task.serial} is awaiting the shared trigger; firing physical trigger");
        _fcs.TriggerConsole.Fire();
    }

    /// <summary>
    /// Two unpaired plans compare once. A compared plan is promoted without re-comparison. One unpaired plan
    /// waits only while an active planning round can still provide a partner; otherwise it single-commits.
    /// </summary>
    public void EvaluateScheduling()
    {
        if (_current != null)
            return;

        var active = ActivePlans();
        if (active.Count == 0)
            return;

        // Cross-batch ordering (R3). The guard above already limits this to the moment no plan owns the shared
        // azimuth lane: a plan that is already executing is never demoted to _next and never interrupted.
        var compared = active.Where(p => p.Compared).ToList();
        if (compared.Count > 0)
        {
            var committed = compared.OrderByDescending(p => p.Task.priority).First();
            var other = active.FirstOrDefault(p => !ReferenceEquals(p, committed));

            // A never-compared plan that outranks the committed one jumps the whole batch order. Two already
            // compared plans do not take this path: the more urgent one is simply picked as committed above.
            if (other is { Compared: false } && other.Task.priority > committed.Task.priority)
            {
                // CommitSingle logs its own [FCS Order] line first; the override line must follow it.
                _fcs.FirePriority.CommitSingle(other, "优先级高于已提交计划");
                MelonLogger.Msg(
                    $"[FCS Order] priority override: {other.Label} (P{other.Task.priority}) fires before " +
                    $"committed {committed.Label} (P{committed.Task.priority})");
                _next = committed;
                SetCurrent(other, promote: false);
                return;
            }

            _next = other;
            SetCurrent(committed, promote: true);
            return;
        }

        if (active.Count >= 2)
        {
            var a = active[0];
            var b = active[1];
            var first = _fcs.FirePriority.ComparePair(a, b);
            _next = ReferenceEquals(first, a) ? b : a;
            SetCurrent(first, promote: false);
            return;
        }

        var single = active[0];
        if (_fcs.Dispatcher.HasPendingOrPlanning)
        {
            _next = single;
            _fcs.FirePriority.MarkWaitingForPair(single);
            return;
        }

        _next = null;
        _fcs.FirePriority.CommitSingle(single, "等待队列为空");
        SetCurrent(single, promote: false);
    }

    private List<FirePlan> ActivePlans()
    {
        var result = new List<FirePlan>(2);
        if (_leftPlan != null && !_leftPlan.Failed && !_leftPlan.ShotObserved)
            result.Add(_leftPlan);
        if (_rightPlan != null && !_rightPlan.Failed && !_rightPlan.ShotObserved)
            result.Add(_rightPlan);
        return result;
    }

    private void SetCurrent(FirePlan plan, bool promote)
    {
        if (_current != null)
            return;

        _current = plan;
        if (ReferenceEquals(_next, plan))
            _next = null;

        if (promote)
            _fcs.FirePriority.PromoteCommitted(plan);

        // Azimuth has no loading dependency. Start immediately after order commit.
        // Review-button dispatch is intentionally independent and is requested later by RunShared.
        _fcs.TrackCoroutine(RunShared(plan));
    }

    private IEnumerator PrepareLocal(FirePlan plan)
    {
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (!IsActive(plan))
            yield break;

        var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;

        // Requisition remains TaskSystem-owned. The persistent transaction is accepted only after resources
        // exist, so F9 during requisition abandons intent but never a half-owned loading transaction.
        var loadingBeforePurchase = _fcs.Loading.GetSnapshot(plan.HostSide);
        var needsPersistentLoad = !loadingBeforePurchase.HasTransaction
                                  && loadingBeforePurchase.PhysicalState != LoadingPhysicalState.LoadedReady;

        if (needsPersistentLoad)
        {
            plan.Task.progress = Progress.SelectingBullet;
            yield return _fcs.SharedResources.Requisition.Acquire(plan.Task.priority);
            try
            {
                var attempts = 0;
                while (gun.RemainingCharges() < plan.Charge && attempts < 10)
                {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    yield return _fcs.PurchaseDeck.BuyPowders();
                    attempts++;
                }

                if (gun.RemainingCharges() < plan.Charge)
                {
                    FailPlan(plan, $"powder unavailable: need {plan.Charge}, have {gun.RemainingCharges()}");
                    yield break;
                }

                if (loadingBeforePurchase.PhysicalState == LoadingPhysicalState.EmptyReady
                    && !gun.HaveBulletInCylinder(plan.Shell))
                {
                    if (!gun.HaveEmptyShellInCylinder())
                    {
                        FailPlan(plan, $"no {plan.Shell.DisplayName()} shell and cylinder has no empty slot");
                        yield break;
                    }

                    yield return _fcs.PurchaseDeck.BuyShell(plan.Shell, plan.Side);
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    if (!gun.HaveBulletInCylinder(plan.Shell))
                    {
                        FailPlan(plan, $"purchase of {plan.Shell.DisplayName()} did not reach cylinder");
                        yield break;
                    }
                }
            }
            finally
            {
                _fcs.SharedResources.Requisition.Release();
            }
        }

        if (!IsActive(plan))
            yield break;

        if (!_fcs.Loading.TryRequest(plan.LoadRequest, out var loadReason))
        {
            FailPlan(plan, $"loading request rejected: {loadReason}");
            yield break;
        }

        var loadingDeadline = FcsRuntimeClock.Now + LoadingObservationTimeoutSeconds;
        while (true)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (!IsActive(plan))
                yield break;

            var snapshot = _fcs.Loading.GetSnapshot(plan.HostSide);
            if (snapshot.Matches(plan.LoadRequest))
                break;

            if (snapshot.TransactionState == LoadingTransactionState.Failed)
            {
                FailPlan(plan, string.IsNullOrWhiteSpace(snapshot.FailureReason)
                    ? "persistent loading transaction failed"
                    : snapshot.FailureReason);
                yield break;
            }

            plan.Task.progress = snapshot.TransactionState switch
            {
                LoadingTransactionState.LoadingShell => Progress.LoadingBullet,
                LoadingTransactionState.LoadingPowder => Progress.LoadingPowder,
                LoadingTransactionState.WaitingLoadedReady => Progress.WaitLoading,
                _ => Progress.WaitLoading,
            };

            if (FcsRuntimeClock.Now >= loadingDeadline)
            {
                FailPlan(plan, $"persistent loading observation timed out; physical={snapshot.PhysicalState}, tx={snapshot.TransactionState}");
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(0.25f);
        }

        // Phase 1 pre-aim (R6). Loading is done and the large elevation slew has not started, so this is the last
        // cheap moment to fold the drift accumulated during loading into the angle we are about to command.
        var aimElevation = plan.Elevation;
        if (ShouldRefreshTracking(plan.Task))
        {
            if (plan.Task.trackEntityId.Length > 0)
                _fcs.MapTable.UpdateEntityMotion(plan.Task);
            _fcs.MapTable.ApplyMotionModel(plan.Task, PreAimPrepSeconds);
            _fcs.MapTable.RefreshSolution(plan.Task);

            var aimSolve = new ElevationSolve();
            yield return ResolveElevation(plan, aimSolve, plan.Task.priority);

            // Preparation-stage liveness only. _current belongs to whichever plan owns the shared azimuth lane,
            // which is very likely this plan's batch partner; testing it here would abort every paired plan.
            if (!IsActive(plan))
                yield break;

            // The Ok gate is not optional: a failed console solve still reports whatever elevation the display
            // held, which can be a finite but stale angle. Without Ok we would lay the gun on garbage.
            if (aimSolve.Ok && Mathf.Abs(aimSolve.Elevation - aimElevation) > TrackElevationEpsilonDegrees)
            {
                MelonLogger.Msg(
                    $"[FCS Track] {plan.Label}: pre-aim elevation refresh {aimElevation:F2}° -> " +
                    $"{aimSolve.Elevation:F2}° ({(aimSolve.Analytic ? "analytic" : "console")})");
                aimElevation = aimSolve.Elevation;
                plan.Task.elevation = aimElevation;
            }
        }

        // Left/right elevation are independent. Start immediately at physical LoadedReady.
        plan.Task.progress = Progress.Aiming;
        yield return gun.SetElevation(aimElevation, ElevationTimeoutSeconds);

        // A different gun may have been physically fired while this plan was still preparing. If the physical
        // settlement consumed this plan, do not turn cancellation into a false elevation failure.
        if (!IsActive(plan))
            yield break;

        if (!gun.LastElevationSucceeded)
        {
            FailPlan(plan, $"elevation did not reach {aimElevation:F1}°");
            yield break;
        }

        plan.LocalReady = true;
        plan.Task.progress = Progress.WaitingForFire;
        MelonLogger.Msg($"[FCS Plan] {plan.Label}: local ready (LoadedReady + elevation)");

        // A non-current ready plan is only a follower candidate for the current shared-fire opportunity. It never
        // inherits the scheduler's _next identity; eligibility is derived fresh from the live fire-wait state.
        _fcs.TrackCoroutine(TryArmReadyFollowerDuringCurrentWait(plan));
    }

    private IEnumerator RunShared(FirePlan plan)
    {
        if (!ReferenceEquals(_current, plan) || !IsActive(plan))
            yield break;

        yield return FcsRuntimeClock.WaitUntilFocused();
        MelonLogger.Msg($"[FCS Plan] {plan.Label}: shared execution start; rotating azimuth immediately");

        yield return _fcs.Turret.SetRotation(plan.Azimuth, 45f, () =>
            plan.Generation != _fcs.FirePriority.Generation
            || plan.Failed
            || !ReferenceEquals(_current, plan)
            || !IsActive(plan));

        if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
            yield break;

        if (!_fcs.Turret.LastRotationSucceeded)
        {
            FailPlan(plan, $"turret could not reach {plan.Azimuth:F1}°");
            yield break;
        }

        plan.AzimuthReady = true;

        while (!plan.LocalReady)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                yield break;
            yield return null;
        }

        var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;

        // Correction baselines for phases 2 and 3. They are captured unconditionally and before any refresh:
        // taken after RefreshSolution the range error would always read zero, and taken inside the trigger gate
        // phase 3 would have no baseline for a task that only becomes re-aimed during the fire wait.
        var appliedAzimuth = plan.Azimuth;
        var appliedElevation = plan.Task.elevation > 0f ? plan.Task.elevation : plan.Elevation;
        var appliedDistance = plan.Task.distance;

        // Phase 2 pre-fire (R6): coarse laying is done, the trigger protocol has not started. Correct only a
        // predicted-impact error large enough to matter; a failed correction is never a plan failure here.
        if (ShouldRefreshTracking(plan.Task))
        {
            if (plan.Task.trackEntityId.Length > 0)
                _fcs.MapTable.UpdateEntityMotion(plan.Task);
            _fcs.MapTable.ApplyMotionModel(plan.Task, PreFirePrepSeconds);
            _fcs.MapTable.RefreshSolution(plan.Task);

            // An explicit re-aim is an order, so it is executed at the ordinary tracking epsilon.
            var significantErrorKm = plan.Task.aimAdjusted ? TrackDistanceEpsilonKm : PreFireSignificantErrorKm;

            var crossErrorKm = Mathf.Abs(Mathf.DeltaAngle(appliedAzimuth, plan.Task.angel))
                               * Mathf.Deg2Rad * plan.Task.distance;
            if (crossErrorKm > significantErrorKm)
            {
                MelonLogger.Msg(
                    $"[FCS Track] {plan.Label}: pre-fire azimuth correction {appliedAzimuth:F2}° -> " +
                    $"{plan.Task.angel:F2}° (cross error {crossErrorKm * 1000f:F0}m)");
                yield return _fcs.Turret.SetRotation(plan.Task.angel, 45f, () =>
                    plan.Failed || !ReferenceEquals(_current, plan) || !IsActive(plan));

                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;
                if (_fcs.Turret.LastRotationSucceeded)
                    appliedAzimuth = plan.Task.angel;
            }

            // Captured before appliedDistance moves, because the log below reports the error that triggered
            // this correction rather than the residual after it.
            var rangeErrorKm = Mathf.Abs(plan.Task.distance - appliedDistance);
            if (rangeErrorKm > significantErrorKm)
            {
                var preFireSolve = new ElevationSolve();
                yield return ResolveElevation(plan, preFireSolve, plan.Task.priority);

                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;

                if (preFireSolve.Ok)
                {
                    appliedDistance = plan.Task.distance;
                    if (Mathf.Abs(preFireSolve.Elevation - appliedElevation) > TrackElevationEpsilonDegrees)
                    {
                        MelonLogger.Msg(
                            $"[FCS Track] {plan.Label}: pre-fire elevation correction {appliedElevation:F2}° -> " +
                            $"{preFireSolve.Elevation:F2}° (range error {rangeErrorKm * 1000f:F0}m)");
                        yield return gun.SetElevation(preFireSolve.Elevation, ElevationTimeoutSeconds);

                        if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                            yield break;
                        if (gun.LastElevationSucceeded)
                        {
                            appliedElevation = preFireSolve.Elevation;
                            plan.Task.elevation = appliedElevation;
                        }
                    }
                }
            }
        }

        var fireWaitToken = 0;
        var autoFireIssued = false;
        PhysicalFireWatch? leftWatch = null;
        PhysicalFireWatch? rightWatch = null;

        try
        {
            yield return _fcs.SharedResources.Trigger.Acquire(plan.Task.priority);
            try
            {
                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;

                // The firing lever is shared and the player may already have manipulated either safety. Capture
                // both baselines before touching any review/arming controls so a player-triggered shot during the
                // console protocol is still reconciled from physical reality.
                leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                rightWatch = BeginFireWatch(_fcs.RightGun, "Right");

                yield return _fcs.TriggerConsole.PrepareForNewFireSolution(plan.Side);

                // Once this gun is physically ready for the shared fire stage, publish only that fact to the
                // independent review-button controller. The controller owns physical switch convergence; the
                // executor preserves the 1.5 s visual lead before arming without waiting for button completion.
                _fcs.TriggerConsole.SetGunReady(plan.Side, true);
                yield return FcsRuntimeClock.WaitForSeconds(ReviewLeadTimeBeforeArmSeconds);

                PollFireWatch(leftWatch);
                PollFireWatch(rightWatch);
                if (leftWatch.Observed || rightWatch.Observed)
                {
                    yield return CompleteSettlementWindow(leftWatch, rightWatch);
                    if (SettleObservedShots(leftWatch.Observed, rightWatch.Observed) > 0)
                        yield break;

                    leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                    rightWatch = BeginFireWatch(_fcs.RightGun, "Right");
                }

                // The scheduled current owns its normal arm path. Follower eligibility is a separate rule and is
                // re-evaluated immediately before the follower touches its own safety.
                yield return _fcs.TriggerConsole.ArmSelected(plan.Side, null);

                // If the player fired during the physical arming operation, settle that reality before issuing an
                // automatic trigger pull. This preserves player authority and prevents an accidental second shot.
                PollFireWatch(leftWatch);
                PollFireWatch(rightWatch);
                if (leftWatch.Observed || rightWatch.Observed)
                {
                    yield return CompleteSettlementWindow(leftWatch, rightWatch);
                    if (SettleObservedShots(leftWatch.Observed, rightWatch.Observed) > 0)
                        yield break;

                    leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                    rightWatch = BeginFireWatch(_fcs.RightGun, "Right");
                }

                fireWaitToken = BeginFireWait(plan);

                // The other active plan may already be LocalReady. Treat it only as a follower candidate and give
                // it one fresh non-blocking eligibility check against the newly opened shared-fire wait.
                var followerCandidate = plan.Side == LeftRight.Left ? _rightPlan : _leftPlan;
                if (followerCandidate != null
                    && !ReferenceEquals(followerCandidate, plan)
                    && followerCandidate.LocalReady)
                {
                    _fcs.TrackCoroutine(TryArmReadyFollowerDuringCurrentWait(followerCandidate));
                }

                if (_fcs.SceneInteractor.AutoFire)
                {
                    yield return FcsRuntimeClock.WaitUntilFocused();
                    _autoFireIssuedForWait = true;
                    _fcs.TriggerConsole.Fire();
                    autoFireIssued = true;
                }
            }
            finally
            {
                _fcs.SharedResources.Trigger.Release();
            }

            if (leftWatch == null || rightWatch == null)
                yield break;

            // Manual fire is intentionally open-ended. Only an Auto Fire attempt gets a deadline so a failed
            // automation can still recover without turning deliberate wait-and-fire missions into task failures.
            float? autoFireDeadline = autoFireIssued || _autoFireIssuedForWait || _fcs.SceneInteractor.AutoFire
                ? FcsRuntimeClock.Now + AutoFireTimeoutSeconds
                : null;
            var resumeGeneration = FcsRuntimeClock.ResumeGeneration;

            // The first manual-wait re-lay happens one interval after the wait opens, not on entry: the gun was
            // just laid, so an immediate correction would only re-solve what pre-fire already applied.
            var nextRelay = FcsRuntimeClock.Now + TrackRelayIntervalSeconds;

            while (true)
            {
                yield return FcsRuntimeClock.WaitUntilFocused();
                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                    yield break;

                if (resumeGeneration != FcsRuntimeClock.ResumeGeneration)
                {
                    resumeGeneration = FcsRuntimeClock.ResumeGeneration;
                    MelonLogger.Msg(
                        $"[FCS Fire] reconciled after focus restore; Left={GunPhysicalState.Read("Left").Summary()}, " +
                        $"Right={GunPhysicalState.Read("Right").Summary()}");
                }

                PollFireWatch(leftWatch);
                PollFireWatch(rightWatch);

                if (leftWatch.Observed || rightWatch.Observed)
                {
                    yield return CompleteSettlementWindow(leftWatch, rightWatch);
                    var consumed = SettleObservedShots(leftWatch.Observed, rightWatch.Observed);
                    if (consumed > 0)
                        yield break;

                    // A gun with no active FirePlan may still have been fired manually. That physical event is real
                    // but must not fail/consume the current plan. Re-baseline both sides and keep waiting.
                    MelonLogger.Msg("[FCS Fire] observed physical fire without a matching active FirePlan; continuing current wait");
                    leftWatch = BeginFireWatch(_fcs.LeftGun, "Left");
                    rightWatch = BeginFireWatch(_fcs.RightGun, "Right");
                }

                if (!autoFireDeadline.HasValue && (_autoFireIssuedForWait || _fcs.SceneInteractor.AutoFire))
                    autoFireDeadline = FcsRuntimeClock.Now + AutoFireTimeoutSeconds;

                if (autoFireDeadline.HasValue && FcsRuntimeClock.Now >= autoFireDeadline.Value)
                {
                    FailPlan(plan, "automatic fire was not observed");
                    yield break;
                }

                // Phase 3 (R6): a manual wait is open-ended, so keep the gun laid on a moving target until the
                // player actually drops the lever. The block sits at the very end of the loop body: a re-lay can
                // itself take seconds, and it must not run ahead of this iteration's shot/timeout detection.
                // No failure here ever fails the plan - the commander decides when to fire.
                if (!autoFireDeadline.HasValue && ShouldRefreshTracking(plan.Task)
                                              && FcsRuntimeClock.Now >= nextRelay)
                {
                    // Tick from the start of the block so the re-lay's own cost is charged to the interval.
                    nextRelay = FcsRuntimeClock.Now + TrackRelayIntervalSeconds;

                    if (plan.Task.trackEntityId.Length > 0)
                        _fcs.MapTable.UpdateEntityMotion(plan.Task);
                    _fcs.MapTable.ApplyMotionModel(plan.Task);
                    _fcs.MapTable.RefreshSolution(plan.Task);

                    // DeltaAngle, not a raw difference: an azimuth pair straddling 0° would otherwise fake a
                    // ~360° error and re-lay the turret every three seconds forever.
                    if (Mathf.Abs(Mathf.DeltaAngle(appliedAzimuth, plan.Task.angel)) > TrackAzimuthEpsilonDegrees)
                    {
                        MelonLogger.Msg(
                            $"[FCS Track] {plan.Label}: manual-wait azimuth re-lay {appliedAzimuth:F2}° -> " +
                            $"{plan.Task.angel:F2}°");
                        yield return _fcs.Turret.SetRotation(plan.Task.angel, 45f, () =>
                            plan.Failed || !ReferenceEquals(_current, plan) || !IsActive(plan));

                        if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                            yield break;
                        if (_fcs.Turret.LastRotationSucceeded)
                            appliedAzimuth = plan.Task.angel;
                    }

                    if (Mathf.Abs(plan.Task.distance - appliedDistance) > TrackDistanceEpsilonKm)
                    {
                        var relaySolve = new ElevationSolve();
                        yield return ResolveElevation(plan, relaySolve, ManualRelayLockPriority);

                        if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                            yield break;

                        if (relaySolve.Ok)
                        {
                            appliedDistance = plan.Task.distance;
                            if (Mathf.Abs(relaySolve.Elevation - appliedElevation) > TrackElevationEpsilonDegrees)
                            {
                                MelonLogger.Msg(
                                    $"[FCS Track] {plan.Label}: manual-wait elevation re-lay " +
                                    $"{appliedElevation:F2}° -> {relaySolve.Elevation:F2}°");
                                yield return gun.SetElevation(relaySolve.Elevation, ElevationTimeoutSeconds);

                                if (!ReferenceEquals(_current, plan) || !IsActive(plan) || plan.Failed)
                                    yield break;
                                if (gun.LastElevationSucceeded)
                                {
                                    appliedElevation = relaySolve.Elevation;
                                    plan.Task.elevation = appliedElevation;
                                }
                            }
                        }
                    }
                }

                yield return FcsRuntimeClock.WaitForSeconds(0.1f);
            }
        }
        finally
        {
            ClearFireWait(plan, fireWaitToken);
        }
    }

    private IEnumerator TryArmReadyFollowerDuringCurrentWait(FirePlan follower)
    {
        yield return FcsRuntimeClock.WaitUntilFocused();

        var current = _current;
        if (current == null || !CanFollowerArm(current, follower, out _))
            yield break;

        // The first check prevents pointless Trigger-lane work. It is not authorization: the live relationship may
        // change while this coroutine waits for the shared physical console lane.
        yield return _fcs.SharedResources.Trigger.Acquire(follower.Task.priority);
        try
        {
            current = _current;
            if (current == null || !CanFollowerArm(current, follower, out var delta))
                yield break;

            MelonLogger.Msg(
                $"[FCS Plan] ready follower arms without waiting: {follower.Label}; current={current.Label}; " +
                $"azimuth delta={delta:F3}°");
            yield return _fcs.TriggerConsole.ArmSelected(follower.Side, null);
        }
        finally
        {
            _fcs.SharedResources.Trigger.Release();
        }
    }

    private bool CanFollowerArm(FirePlan current, FirePlan follower, out float azimuthDelta)
    {
        azimuthDelta = float.PositiveInfinity;

        if (!ReferenceEquals(_current, current)
            || ReferenceEquals(current, follower)
            || current.Side == follower.Side
            || !IsActive(current)
            || !IsActive(follower)
            || !current.LocalReady
            || !current.AzimuthReady
            || !follower.LocalReady
            || !ReferenceEquals(_fireWaitOwner, current)
            || _fireWaitGeneration != current.Generation
            || _autoFireIssuedForWait)
        {
            return false;
        }

        azimuthDelta = Mathf.Abs(Mathf.DeltaAngle(current.Azimuth, follower.Azimuth));
        return azimuthDelta <= SameAzimuthToleranceDegrees;
    }

    /// <summary>
    /// Whether this task has anything an execution-stage refresh could change. A tracked entity or a motion model
    /// moves on its own; an agent re-aim moves a task that is otherwise static, so it opens the same gate.
    /// </summary>
    private static bool ShouldRefreshTracking(ArtilleryTask task)
        => task.trackEntityId.Length > 0 || task.hasMotion || task.aimAdjusted;

    /// <summary>
    /// Closed-form elevation for the game's linear ballistics: elevation(deg) = distance(km) * 12 / charge,
    /// capped at 60°. Outside that envelope there is no analytic answer and the physical desk has to decide.
    /// </summary>
    public static bool TryAnalyticElevation(int charge, float distanceKm, out float elevationDeg)
    {
        elevationDeg = float.NaN;
        if (charge <= 0 || distanceKm <= 0.01f)
            return false;

        var candidate = distanceKm * 12f / charge;
        if (candidate > 60.01f)
            return false;

        elevationDeg = Mathf.Min(candidate, 60f);
        return true;
    }

    /// <summary>
    /// Single elevation entry point for every execution-stage refresh. Both branches take the same input - the
    /// plan's committed charge and the task's freshly refreshed range - so the analytic and console answers can
    /// never disagree about what was asked.
    ///
    /// Deliberate exception to the "re-check liveness after every yield" rule: once this coroutine starts it runs
    /// to completion. Bailing out mid-way would release the ballistic desk with half-written dials and leave
    /// Ok = false, which the caller could not tell apart from a genuine solve failure. Liveness is the caller's
    /// job, immediately after this returns.
    /// </summary>
    private IEnumerator ResolveElevation(FirePlan plan, ElevationSolve result, int lockPriority)
    {
        result.Ok = false;
        result.Analytic = false;
        result.Elevation = float.NaN;

        if (TryAnalyticElevation(plan.Charge, plan.Task.distance, out var analytic))
        {
            result.Ok = true;
            result.Analytic = true;
            result.Elevation = analytic;
            yield break;
        }

        yield return _fcs.SharedResources.Ballistic.Acquire(lockPriority);
        try
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            yield return _fcs.BallisticCalculator.SetDistance(plan.Task.distance);
            yield return _fcs.BallisticCalculator.SetDirection(plan.Task.angel);
            yield return _fcs.BallisticCalculator.SetCharge(plan.Charge);
            yield return _fcs.BallisticCalculator.SetShellType(plan.Task.bulletType);
            yield return _fcs.BallisticCalculator.Calculate();

            // The reading is stored even when the solve failed, matching the console's own behaviour; callers
            // gate on Ok before believing it.
            var elevation = _fcs.BallisticCalculator.GetElevation();
            result.Elevation = elevation;
            result.Ok = _fcs.BallisticCalculator.LastCalculationSucceeded
                        && !float.IsNaN(elevation)
                        && !float.IsInfinity(elevation);
        }
        finally
        {
            _fcs.SharedResources.Ballistic.Release();
        }
    }

    private PhysicalFireWatch BeginFireWatch(GunSystem gun, string sideName)
    {
        var physical = GunPhysicalState.Read(sideName);
        var watch = new PhysicalFireWatch(
            gun,
            sideName,
            gun.BulletInChamber(),
            physical.PendingReload);

        MelonLogger.Msg(
            $"[FCS Fire] {sideName} baseline: chamber={watch.ChamberAtStart ?? "empty"}, " +
            $"pendingReload={watch.PendingReloadAtStart}, physical={physical.Summary()}");
        return watch;
    }

    private static void PollFireWatch(PhysicalFireWatch watch)
    {
        if (watch.Observed)
            return;

        var physical = GunPhysicalState.Read(watch.SideName);
        var chamberNow = watch.Gun.BulletInChamber();

        // Observe a transition from the baseline, not merely a state that was already true when the shared wait
        // began. This matters because the other gun may legitimately still be recovering from an older shot.
        var pendingReloadTransition = !watch.PendingReloadAtStart && physical.PendingReload;
        var chamberTransition = watch.ChamberAtStart != null && chamberNow == null;
        if (pendingReloadTransition || chamberTransition)
        {
            watch.Observed = true;
            MelonLogger.Msg(
                $"[FCS Fire] {watch.SideName} shot observed; baseline={watch.ChamberAtStart ?? "empty"}, " +
                $"now={chamberNow ?? "empty"}, pendingReload={physical.PendingReload}, physical={physical.Summary()}");
        }
    }

    private IEnumerator CompleteSettlementWindow(PhysicalFireWatch leftWatch, PhysicalFireWatch rightWatch)
    {
        for (var i = 0; i < FireSettlementBufferFrames; i++)
            yield return null;

        yield return FcsRuntimeClock.WaitUntilFocused();
        PollFireWatch(leftWatch);
        PollFireWatch(rightWatch);
    }

    /// <summary>
    /// Consume every active FirePlan whose gun actually fired in this physical event. current/next are discarded as
    /// execution pointers and rebuilt from the remaining plans afterward; they never dictate the physical result.
    /// </summary>
    private int SettleObservedShots(bool leftFired, bool rightFired)
    {
        var left = leftFired && _leftPlan != null && IsActive(_leftPlan) ? _leftPlan : null;
        var right = rightFired && _rightPlan != null && IsActive(_rightPlan) ? _rightPlan : null;
        if (left == null && right == null)
            return 0;

        var consumed = 0;
        _current = null;
        _next = null;
        ClearAllFireWait();

        if (left != null)
        {
            CompletePlanFromObservedShot(left);
            consumed++;
        }

        if (right != null && !ReferenceEquals(right, left))
        {
            CompletePlanFromObservedShot(right);
            consumed++;
        }

        MelonLogger.Msg(
            $"[FCS Fire] physical settlement complete: Left={(left != null ? $"#{left.Task.serial}" : "-")}, " +
            $"Right={(right != null ? $"#{right.Task.serial}" : "-")}; rebuilding scheduling from reality");

        // Batch first, then trigger planning/scheduling once. FirePlanner will re-read physical/loading state; a
        // just-fired gun therefore remains Pending/recovery-gated even though its FirePlan slot is already free.
        _fcs.Dispatcher.TryDispatch();
        EvaluateScheduling();
        return consumed;
    }

    private void CompletePlanFromObservedShot(FirePlan plan)
    {
        if (plan.CompletionHandled)
            return;

        // A player may physically fire a non-current plan while its preparation coroutine is still running.
        // Stop that old intent so it cannot continue driving elevation after the physical round is already gone.
        if (!plan.LocalReady)
            CancelPreparation(plan);

        plan.ShotObserved = true;
        _fcs.FirePriority.MarkShot(plan);
        plan.Task.progress = Progress.Finished;
        plan.Task.failureReason = "";
        _fcs.Dispatcher.RecordTaskResult(plan.Task);
        ReleaseGunSlot(plan, notify: false);
    }

    private void CancelPreparation(FirePlan plan)
    {
        if (!_prepareCoroutines.TryGetValue(plan, out var handle))
            return;

        try
        {
            MelonCoroutines.Stop(handle);
            MelonLogger.Msg($"[FCS Plan] stopped obsolete preparation after physical fire: {plan.Label}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS Plan] failed to stop obsolete preparation for {plan.Label}: {ex.Message}");
        }
        finally
        {
            _prepareCoroutines.Remove(plan);
        }
    }

    private int BeginFireWait(FirePlan plan)
    {
        var token = ++_fireWaitSerial;
        _activeFireWaitSerial = token;
        _fireWaitOwner = plan;
        _fireWaitGeneration = plan.Generation;
        _autoFireIssuedForWait = false;
        return token;
    }

    private void ClearFireWait(FirePlan plan, int token)
    {
        if (token == 0
            || token != _activeFireWaitSerial
            || !ReferenceEquals(_fireWaitOwner, plan))
        {
            return;
        }

        ClearAllFireWait();
    }

    private void ClearAllFireWait()
    {
        _fireWaitOwner = null;
        _fireWaitGeneration = -1;
        _activeFireWaitSerial = 0;
        _autoFireIssuedForWait = false;
    }

    private void FailPlan(FirePlan plan, string reason)
    {
        if (plan.CompletionHandled)
            return;

        // Deliberately behind the settlement check above: a plan that already completed must never enter
        // recovery, or an already fired round would be re-queued as if it were still loadable.
        if (TryRecoverPowderFailure(plan, reason))
            return;

        plan.Failed = true;
        plan.FailureReason = reason;
        plan.Task.progress = Progress.Failed;
        plan.Task.failureReason = reason;
        MelonLogger.Error($"[FCS Plan] {plan.Label} failed: {reason}");

        if (ReferenceEquals(_fireWaitOwner, plan))
            ClearAllFireWait();
        if (ReferenceEquals(_current, plan))
            _current = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        _fcs.Dispatcher.RecordTaskResult(plan.Task);
        ReleaseGunSlot(plan, notify: true);
    }

    /// <summary>
    /// R7 powder-failure recovery. A committed charge cannot be topped up or removed - the only way to clear the
    /// chamber is to fire it - so a mismatch is answered either by shooting the target with the charge we
    /// actually got, or by throwing that round away on the same bearing and reloading from scratch. A dispenser
    /// hiccup is just a restock window and is retried a bounded number of times.
    /// </summary>
    private bool TryRecoverPowderFailure(FirePlan plan, string reason)
    {
        var task = plan.Task;

        var physicalCharge = 0;
        var mismatch = PowderCommitMismatchPattern.Match(reason);
        if (mismatch.Success)
            int.TryParse(mismatch.Groups[2].Value, out physicalCharge);

        if (physicalCharge >= 1)
        {
            if (task.loadRetryCount >= PowderCommitRetryLimit)
                return false;

            task.loadRetryCount++;
            DetachForRequeue(plan, reason);

            var reachKm = physicalCharge * 5f;
            if (task.distance <= reachKm + 0.01f)
            {
                MelonLogger.Warning(
                    $"[FCS Plan] {plan.Label}: committed C{physicalCharge} still reaches {task.distance:F2}km — " +
                    $"requeued to fire on the actual charge");
            }
            else
            {
                // The round in the chamber can only leave by being fired. Dump it short on the same bearing line
                // so the gun becomes loadable again; give it a small priority bump so it does not sit behind the
                // task that is waiting for the very barrel it is blocking.
                var dumpRangeKm = reachKm * ChamberClearingRangeFactor;
                var dump = new ArtilleryTask
                {
                    bulletType = task.bulletType,
                    priority = Math.Min(100, task.priority + 5),
                    hasAimPoint = true,
                    aimLocal = _fcs.MapTable.ShortenedAim(task, dumpRangeKm),
                };
                _fcs.MapTable.RefreshSolution(dump);

                // Queued before the warning so the dump task already carries the serial the warning quotes.
                _fcs.Dispatcher.EnqueueTask(dump);
                MelonLogger.Warning(
                    $"[FCS Plan] {plan.Label}: chamber committed C{physicalCharge}, target {task.distance:F2}km " +
                    $"out of its reach — queued chamber-clearing shot #{dump.serial} at {dumpRangeKm:F1}km same " +
                    $"bearing; original requeued for fresh load");
            }
        }
        else if (reason.Contains("powder dispenser") && task.loadRetryCount < PowderDispenserRetryLimit)
        {
            task.loadRetryCount++;
            DetachForRequeue(plan, reason);
            MelonLogger.Warning(
                $"[FCS Plan] {plan.Label}: transient dispenser failure, " +
                $"retry {task.loadRetryCount}/{PowderDispenserRetryLimit} — requeued");
        }
        else
        {
            return false;
        }

        _fcs.Dispatcher.EnqueueTask(task);
        return true;
    }

    /// <summary>
    /// Tear a plan down without failing its task, so the task can be planned again. Unlike urgent preemption this
    /// notifies immediately - the gun is genuinely idle and the requeued task wants the next planning round - and
    /// it clears the execution pointers explicitly rather than relying on slot release to do it.
    /// </summary>
    private void DetachForRequeue(FirePlan plan, string reason)
    {
        plan.Failed = true;
        plan.FailureReason = reason;
        CancelPreparation(plan);

        if (ReferenceEquals(_fireWaitOwner, plan))
            ClearAllFireWait();
        if (ReferenceEquals(_current, plan))
            _current = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        ReleaseGunSlot(plan, notify: true);

        plan.Task.progress = Progress.Pending;
        plan.Task.failureReason = "";
        plan.Task.pendingHint = PendingHint.None;
    }

    private void ReleaseGunSlot(FirePlan plan, bool notify)
    {
        if (plan.CompletionHandled)
            return;

        plan.CompletionHandled = true;
        _prepareCoroutines.Remove(plan);

        var gun = plan.Side == LeftRight.Left ? _fcs.LeftGun : _fcs.RightGun;
        gun.ReleaseElevationOverride();
        _fcs.TriggerConsole.SetGunReady(plan.Side, false);

        if (plan.Side == LeftRight.Left && ReferenceEquals(_leftPlan, plan))
            _leftPlan = null;
        if (plan.Side == LeftRight.Right && ReferenceEquals(_rightPlan, plan))
            _rightPlan = null;
        if (ReferenceEquals(_current, plan))
            _current = null;
        if (ReferenceEquals(_next, plan))
            _next = null;

        if (!notify)
            return;

        _fcs.Dispatcher.TryDispatch();
        EvaluateScheduling();
    }

    private bool IsActive(FirePlan plan)
    {
        return plan.Generation == _fcs.FirePriority.Generation
               && ReferenceEquals(GetPlan(plan.Side), plan)
               && !plan.CompletionHandled;
    }

    /// <summary>
    /// One elevation answer. Analytic records which branch produced it, purely for the refresh log.
    /// </summary>
    private sealed class ElevationSolve
    {
        public bool Ok { get; set; }
        public float Elevation { get; set; } = float.NaN;
        public bool Analytic { get; set; }
    }

    private sealed class PhysicalFireWatch
    {
        public GunSystem Gun { get; }
        public string SideName { get; }
        public string? ChamberAtStart { get; }
        public bool PendingReloadAtStart { get; }
        public bool Observed { get; set; }

        public PhysicalFireWatch(
            GunSystem gun,
            string sideName,
            string? chamberAtStart,
            bool pendingReloadAtStart)
        {
            Gun = gun;
            SideName = sideName;
            ChamberAtStart = chamberAtStart;
            PendingReloadAtStart = pendingReloadAtStart;
        }
    }
}
