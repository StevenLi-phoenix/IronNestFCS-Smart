using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.Execution;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Infrastructure;
using IronNestFCS.Logic.Localization;
using IronNestFCS.Logic.Scheduling;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

public enum LeftRight
{
    Left,
    Right,
}

/// <summary>
/// Reloadable TaskSystem composition root. Persistent physical loading is injected from the stable Host.
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    private HarmonyInstance? _harmony;
    private readonly List<object> _runningCoroutines = new();
    private readonly SceneExposureService _sceneExposure;
    private int _lastResumeGeneration;

    internal ILoadingSystem Loading { get; }
    internal FcsSceneInteractor SceneInteractor { get; private set; }
    internal PurchaseDeck PurchaseDeck { get; } = new();
    internal SharedConsoleCoordinator SharedResources { get; }
    internal TaskDispatcher Dispatcher { get; }
    internal FirePriorityCoordinator FirePriority { get; }
    internal FirePlanner Planner { get; }
    internal FirePlanExecutor PlanExecutor { get; }

    public readonly MapTable MapTable = new();
    public readonly BallisticCalculator BallisticCalculator = new();
    public readonly GunSystem LeftGun = new();
    public readonly GunSystem RightGun = new();
    public readonly Turret Turret = new();
    public readonly TriggerConsole TriggerConsole = new();

    public ArtilleryTask? LeftTask => PlanExecutor.LeftTask;
    public ArtilleryTask? RightTask => PlanExecutor.RightTask;
    public int PendingCount => Dispatcher.PendingCount;
    public Queue<ArtilleryTask> QueueCan => Dispatcher.QueueSnapshot;
    public Queue<ArtilleryTask> RecentTasks => Dispatcher.RecentSnapshot;
    public bool AutoFireEnabled => SceneInteractor.AutoFire;
    public bool MaxChargeEnabled => SceneInteractor.maxCharge;
    public int CompletedTaskCount => Dispatcher.CompletedTaskCount;
    public int SuccessfulTaskCount => Dispatcher.SuccessfulTaskCount;
    public int FailedTaskCount => Dispatcher.FailedTaskCount;
    // External bridges poll this every couple of seconds and compare it against the previous read to detect a
    // new purchase result, so it must stay a property and must be "" (never null) before the first request lands.
    public string ConsoleCardRequestResult => SharedResources.LastCardRequestResult;
    public string FirePriorityStatusText => FirePriority.StatusText;
    public string FirePriorityLeftDetail => FirePriority.LeftDetail;
    public string FirePriorityRightDetail => FirePriority.RightDetail;

    public bool IsBound { get; private set; }

    public FSC(IFcsHostServices hostServices)
    {
        Loading = hostServices.Loading;
        SceneInteractor = new FcsSceneInteractor(this);
        SharedResources = new SharedConsoleCoordinator(this);
        FirePriority = new FirePriorityCoordinator();
        PlanExecutor = new FirePlanExecutor(this);
        Planner = new FirePlanner(this);
        Dispatcher = new TaskDispatcher(this);
        _sceneExposure = new SceneExposureService(this);
    }

    private static bool TryBindSafe(string name, Func<bool> binder)
    {
        try
        {
            var ok = binder();
            if (!ok)
                MelonLogger.Warning($"[FCS] Bind failed: {name}");
            return ok;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] Bind exception in {name}: {ex}");
            return false;
        }
    }

    public bool TryBind()
    {
        SceneInteractor = new FcsSceneInteractor(this);
        _harmony = new HarmonyInstance(HarmonyId);

        SharedResources.Reset();
        FcsRuntimeClock.Reset();
        _lastResumeGeneration = FcsRuntimeClock.ResumeGeneration;
        TimeToImpactReader.Reset();
        FcsLocalization.ResetGameLanguage();
        PlanExecutor.DisposeState();

        IsBound = Loading.IsBound
                  && TryBindSafe(nameof(MapTable), MapTable.TryBind)
                  && TryBindSafe(nameof(BallisticCalculator), BallisticCalculator.TryBind)
                  && TryBindSafe("LeftGun", () => LeftGun.TryBind("Left"))
                  && TryBindSafe("RightGun", () => RightGun.TryBind("Right"))
                  && TryBindSafe(nameof(PurchaseDeck), PurchaseDeck.TryBind)
                  && TryBindSafe(nameof(Turret), Turret.TryBind)
                  && TryBindSafe(nameof(TriggerConsole), TriggerConsole.TryBind);

        if (!Loading.IsBound)
            MelonLogger.Warning("[FCS] Persistent LoadingSystem is not bound.");

        if (IsBound)
            FcsLocalization.BindGameLanguage();
        FirePriority.Reset();

        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound)
        {
            SceneInteractor.Initialize();
            TrackCoroutine(SharedResources.ResetFireControlsAfterBind());
            TrackCoroutine(TriggerConsole.ReviewStateLoop());
            TrackCoroutine(SharedResources.ReplenishPowderLoop());
            TrackCoroutine(GunTargetMarkerLoop());
        }

        return IsBound;
    }

    public void Update()
    {
        FcsRuntimeClock.Update();
        if (!FcsRuntimeClock.IsFocused)
            return;

        if (_lastResumeGeneration != FcsRuntimeClock.ResumeGeneration)
        {
            _lastResumeGeneration = FcsRuntimeClock.ResumeGeneration;
            Dispatcher.TryDispatch();
        }

        FcsLocalization.TickGameLanguage();
        if (PurchaseDeck.SyncTick())
            SceneInteractor.RefreshBulletTypeButtons();
        SceneInteractor.Update();
        PlanExecutor.Tick();

        // A time-limited task must expire even while no planning round runs; the dispatcher throttles the
        // scan internally, so pumping it every focused frame costs nothing. It runs after this frame's
        // interaction and execution pumps so a task those admit onto a gun is safe from the sweep.
        Dispatcher.SweepExpiredTasks();

        CaptureEstimatedFlightTime(LeftRight.Left);
        CaptureEstimatedFlightTime(LeftRight.Right);
    }

    private void CaptureEstimatedFlightTime(LeftRight side)
    {
        var plan = PlanExecutor.GetPlan(side);
        if (plan == null
            || plan.Task.progress != Progress.WaitingForFire
            || !float.IsNaN(plan.EstimatedFlightSeconds))
        {
            return;
        }

        if (TimeToImpactReader.TryReadEstimatedSeconds(side, out var seconds))
            plan.TrySetEstimatedFlightSeconds(seconds);
    }

    public void Dispose()
    {
        foreach (var handle in _runningCoroutines)
        {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        _runningCoroutines.Clear();

        LeftGun.ReleaseElevationOverride();
        RightGun.ReleaseElevationOverride();

        Dispatcher.DisposeState();
        PlanExecutor.DisposeState();
        FirePriority.Reset();
        TimeToImpactReader.Reset();
        FcsLocalization.ResetGameLanguage();

        SceneInteractor.ShutDown();

        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    internal object TrackCoroutine(IEnumerator routine)
    {
        var handle = MelonCoroutines.Start(routine);
        _runningCoroutines.Add(handle);
        return handle;
    }

    public void EnqueueTask(ArtilleryTask task) => Dispatcher.EnqueueTask(task);

    /// <summary>
    /// Re-aims a task that the commander already handed to the FCS. Guns are searched before the pending queue so
    /// a task that is already being executed is corrected in place instead of matching a stale queue entry.
    /// Never returns null: the machine-readable contract is that success starts with a lowercase "ok".
    /// </summary>
    public string AdjustTaskAim(int serial, float localX, float localY)
    {
        var left = LeftTask;
        if (left != null && left.serial == serial && IsAdjustableOnGun(left))
            return MapTable.AdjustAim(left, localX, localY, true);

        var right = RightTask;
        if (right != null && right.serial == serial && IsAdjustableOnGun(right))
            return MapTable.AdjustAim(right, localX, localY, true);

        foreach (var pending in Dispatcher.QueueSnapshot)
        {
            if (pending.serial == serial)
                return MapTable.AdjustAim(pending, localX, localY, false);
        }

        return $"no adjustable task #{serial} — 不在等待队列也不在炮位上(已出膛/已完成/已清除)";
    }

    /// <summary>A shell that already left the barrel (or a finished/failed task) can no longer be re-aimed.</summary>
    private static bool IsAdjustableOnGun(ArtilleryTask task) =>
        task.progress != Progress.Finished && task.progress != Progress.Failed;

    /// <summary>
    /// Cancels a task that is still waiting in the queue. Anything already on a gun is left to the urgent
    /// preemption path. Returns null when no such pending task exists - external callers distinguish
    /// null ("no pending task") from a description string ("cancelled"), so this must not report failure as text.
    /// </summary>
    public string? CancelPendingTask(int serial) => Dispatcher.CancelPendingBySerial(serial);

    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing)
        => RequestConsoleCard(cardId, bearingDeg, hasBearing, 0f, false, 50, null);

    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, int priority)
        => RequestConsoleCard(cardId, bearingDeg, hasBearing, 0f, false, priority, null);

    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, int priority, string? startGrid)
        => RequestConsoleCard(cardId, bearingDeg, hasBearing, 0f, false, priority, startGrid);

    /// <summary>
    /// Queues an external buy-card request onto the requisition console. The bool flags carry "value present",
    /// so a missing bearing/distance must become null: a non-null BearingDeg drags every plain card through the
    /// recon-dial wait and fails it.
    /// </summary>
    public string RequestConsoleCard(
        string cardId,
        float bearingDeg,
        bool hasBearing,
        float distanceKm,
        bool hasDistance,
        int priority,
        string? startGrid)
    {
        SharedResources.EnqueueCardRequest(new ConsoleCardRequest
        {
            CardId = cardId,
            BearingDeg = hasBearing ? bearingDeg : (float?)null,
            DistanceKm = hasDistance ? distanceKm : (float?)null,
            // Whitespace-only means "not given": it must not reach the start-grid regex, and it must not
            // produce a trailing ", start " in the acknowledgement below.
            StartGrid = string.IsNullOrWhiteSpace(startGrid) ? null : startGrid,
            Priority = priority,
        });

        return $"queued to FCS console coordinator (P{priority}"
               + (hasDistance ? $", dist {distanceKm:F1}km" : "")
               + (string.IsNullOrWhiteSpace(startGrid) ? "" : $", start {startGrid}") + ")";
    }

    /// <summary>
    /// Keeps map markers 9 and 10 on the aim points of the left/right gun. T1-T8 belong to the player and are
    /// never touched - that discipline lives here, in the only call site, not inside SetGunTargetMarker.
    /// The loop is deliberately unconditional (invariant 1's named exception in the spec): a transient unbound
    /// frame must not stop it forever, so its lifetime is owned entirely by TrackCoroutine.
    /// </summary>
    private IEnumerator GunTargetMarkerLoop()
    {
        while (true)
        {
            yield return FcsRuntimeClock.WaitForSeconds(0.5f);
            MapTable.SetGunTargetMarker(9, ActiveAim(LeftTask));
            MapTable.SetGunTargetMarker(10, ActiveAim(RightTask));
        }
    }

    /// <summary>
    /// The aim point a gun marker should show, or null to leave the marker where it is. After the shot the
    /// marker stays on the planned impact point, which is exactly the "where is the shell going" indication.
    /// </summary>
    private static Vector3? ActiveAim(ArtilleryTask? task)
    {
        if (task == null || !task.hasAimPoint)
            return null;
        if (task.progress == Progress.Finished || task.progress == Progress.Failed)
            return null;
        return task.aimLocal;
    }

    public IEnumerator ExposeAllEntities() => _sceneExposure.ExposeAllEntities();
}
