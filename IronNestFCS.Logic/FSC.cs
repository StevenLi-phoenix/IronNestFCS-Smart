using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.Execution;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Infrastructure;
using IronNestFCS.Logic.Localization;
using IronNestFCS.Logic.Scheduling;
using MelonLoader;

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
            // Card requests are event-driven: EnqueueCardRequest kicks its own drain coroutine.
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

    // Task handle discipline: the unique serial (#N) is the ONLY external handle for
    // cancel/adjust — targetId is the recycled map-marker id and repeats.
    public string? CancelPendingTask(int serial) => Dispatcher.CancelPendingBySerial(serial);

    /// <summary>
    /// LLM-initiated aim adjustment on an already-queued or in-preparation task (by unique
    /// serial #N). Non-blocking by design: execution never waits for adjustments — with no
    /// adjustment the task fires on its original solution; with one, the staged re-solve
    /// pipeline lays the new point on its next pass. WaitingForFire under auto-fire may
    /// already be too late.
    /// </summary>
    public string AdjustTaskAim(int serial, float localX, float localY)
    {
        if (LeftTask is { } lt && lt.serial == serial && lt.progress is not (Progress.Finished or Progress.Failed))
            return MapTable.AdjustAim(lt, localX, localY, onGun: true);
        if (RightTask is { } rt && rt.serial == serial && rt.progress is not (Progress.Finished or Progress.Failed))
            return MapTable.AdjustAim(rt, localX, localY, onGun: true);
        foreach (var task in Dispatcher.QueueSnapshot)
            if (task.serial == serial)
                return MapTable.AdjustAim(task, localX, localY, onGun: false);
        return $"no adjustable task #{serial} — 不在等待队列也不在炮位上(已出膛/已完成/已清除)";
    }

    /// <summary>External punchcard purchase (e.g. scout plane): queued into the console coordinator.</summary>
    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing)
        => RequestConsoleCard(cardId, bearingDeg, hasBearing, 50);

    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, int priority)
        => RequestConsoleCard(cardId, bearingDeg, hasBearing, priority, null);

    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, int priority, string? startGrid)
        => RequestConsoleCard(cardId, bearingDeg, hasBearing, 0f, false, priority, startGrid);

    /// <summary>Full form: distance feeds cards like MoveDirection (bearing + distance dials).</summary>
    public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing,
        float distanceKm, bool hasDistance, int priority, string? startGrid)
    {
        SharedResources.EnqueueCardRequest(new Infrastructure.ConsoleCardRequest
        {
            CardId = cardId,
            BearingDeg = hasBearing ? bearingDeg : null,
            DistanceKm = hasDistance ? distanceKm : null,
            StartGrid = string.IsNullOrWhiteSpace(startGrid) ? null : startGrid,
            Priority = priority,
        });
        return $"queued to FCS console coordinator (P{priority}" +
               (hasDistance ? $", dist {distanceKm:F1}km" : "") +
               (string.IsNullOrWhiteSpace(startGrid) ? "" : $", start {startGrid}") + ")";
    }

    public string ConsoleCardRequestResult => SharedResources.LastCardRequestResult;

    public IEnumerator ExposeAllEntities() => _sceneExposure.ExposeAllEntities();
}
