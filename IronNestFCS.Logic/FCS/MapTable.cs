using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private const float MarkerSampleIntervalSeconds = 0.1f;
    private const float MarkerStabilizeTimeoutSeconds = 2f;
    private const float MarkerStableToleranceLocal = 0.0025f;
    private const int MarkerStableSampleCount = 3;

    private Transform? turretLocation;
    private Transform? mapSurface;
    private Dictionary<int, Transform> artilleries = new();
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        turretLocation = null;
        mapSurface = null;
        fireMissionRoot = null;
        fireMission = null;

        var turretLocationObject = GameObject.Find("TurretLocation");
        if (turretLocationObject == null) {
            MelonLogger.Warning("[FCS] 未找到 TurretLocation，当前场景尚未就绪");
            return false;
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface，当前场景尚未就绪");
            return false;
        }

        turretLocation = turretLocationObject.transform;
        mapSurface = mapObject.transform;
        var map = mapSurface;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") continue;
            var tmp = t.GetComponentInChildren<Il2CppTMPro.TextMeshPro>();
            if (tmp == null) continue;
            if (!int.TryParse(tmp.text, out var id)) continue;
            artilleries[id] = t;
        }

        if (artilleries.Count == 0) {
            MelonLogger.Warning("[FCS] 未找到任何 MapToken_Artillery，当前场景尚未就绪");
            return false;
        }

        MelonLogger.Msg($"[FCS] 找到 TurretLocation: {turretLocation}, Artilleries: {artilleries.Count}");

        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject != null) {
            fireMissionRoot = fireMissionObject.transform;
            fireMission = fireMissionRoot.GetComponent<FireMission>();
            if (fireMission == null) {
                MelonLogger.Warning("[FCS] Fire Mission Root 存在但缺少 FireMission 组件；调试实体功能不可用");
            }
        }
        else {
            MelonLogger.Msg("[FCS] Fire Mission Root 不存在；忽略（不影响地图标记火控）");
        }

        return true;
    }

    private ArtilleryTask BuildMarkTarget(Vector3 artilleryLocalPosition, Vector3 target) {
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = artilleryLocalPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f)
        };
    }

    // The player's draggable turret piece on the map table ("Player Turret Piece", the
    // single miniature the game spawns under the Draggable Surface). Its live position is
    // the inferred ground truth for the firing origin: wherever the commander believes the
    // turret is. A wrong belief produces wrong solutions — by design.
    public const string PlayerTurretPieceName = "Player Turret Piece";
    private Transform? turretMapModel;

    private Vector3 GetTurretLocalOnMap() {
        if (turretMapModel == null && mapSurface != null) {
            turretMapModel = mapSurface.Find(PlayerTurretPieceName);
            if (turretMapModel != null)
                MelonLogger.Msg(
                    $"[FCS] firing origin bound to '{PlayerTurretPieceName}' local=({turretMapModel.localPosition.x:F3},{turretMapModel.localPosition.y:F3})");
        }
        if (turretMapModel != null)
            return turretMapModel.localPosition;
        if (turretLocation == null || mapSurface == null)
            return Vector3.zero;
        return mapSurface.InverseTransformPoint(turretLocation.position);
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turretLocation == null || mapSurface == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: TurretLocation or map surface unbound");
            return null;
        }

        if (!artilleries.TryGetValue(index, out var artillery)) {
            MelonLogger.Error($"[FCS] GetMarkTarget: artillery marker T{index} not found");
            return null;
        }

        var target = artillery.localPosition - GetTurretLocalOnMap();
        var task = BuildMarkTarget(artillery.localPosition, target);
        task.hasAimPoint = true;
        task.aimLocal = artillery.localPosition;
        return task;
    }

    // ---- Moving-target motion models ----

    private const float TrackPrepSeconds = 45f;        // buy/load/aim before the shot leaves
    private const float ShellSpeedKmPerSec = 0.4f;     // coarse average for flight-time estimate
    private const float MaxLeadLocalUnits = 3f / 3.8164f;         // never lead further than 3 km
    private const float TrackingLostAfterSeconds = 90f;           // fog extrapolation gets flagged
    private const float MapLocalMinX = (-1f - 10.016f) / 3.8164f; // km-frame envelope, local units
    private const float MapLocalMaxX = (27f - 10.016f) / 3.8164f;
    private const float MapLocalMinY = (-1f - 5.235f) / 3.8164f;
    private const float MapLocalMaxY = (16f - 5.235f) / 3.8164f;

    /// <summary>
    /// Game clock in seconds-of-day — the 24h world clock (GenericTimerSceneSync, the same
    /// clock the telegraph references and the bridge stamps on agent events). Falls back to
    /// the mission stopwatch, then realtime.
    /// </summary>
    private static GenericTimerSceneSync? _worldClock;
    public static float MissionNow {
        get {
            try {
                if (_worldClock == null)
                    foreach (var sync in UnityEngine.Object.FindObjectsOfType<GenericTimerSceneSync>())
                        if (_worldClock == null || sync.CurrentTime > _worldClock.CurrentTime)
                            _worldClock = sync;
                if (_worldClock != null && _worldClock.CurrentTime > 0f)
                    return _worldClock.CurrentTime;
            }
            catch { _worldClock = null; }
            try {
                var tracker = MissionStatsTracker.Instance;
                if (tracker != null && tracker.timerRunning)
                    return tracker.timerValue;
            }
            catch { }
            return Time.realtimeSinceStartup;
        }
    }

    private readonly Dictionary<string, (Vector3 local, float t)> _entitySamples = new();

    /// <summary>
    /// Refit a tracked task's motion model from the live entity. While visible: origin/vel
    /// re-sampled (velocity low-passed to damp map jitter). Fogged or dead: the existing
    /// model keeps extrapolating; trackingLost flags stale models.
    /// </summary>
    public void UpdateEntityMotion(ArtilleryTask task) {
        if (task.trackEntityId.Length == 0 || fireMissionRoot == null || mapSurface == null)
            return;

        var now = MissionNow;
        task.trackingLost = task.hasMotion && now - task.motionT0 > TrackingLostAfterSeconds;

        for (var i = 0; i < fireMissionRoot.childCount; i++) {
            var child = fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;
            MapEntity? entity = null;
            try { entity = loc.Entity; } catch { }
            if (entity == null) continue;
            string? id = null, rawId = null;
            try { id = entity.ID; rawId = entity.RawID; } catch { }
            if (id != task.trackEntityId && rawId != task.trackEntityId) continue;

            bool visible = false, alive = true;
            try {
                alive = entity.IsAlive;
                visible = loc.VisualRoot != null && loc.VisualRoot.activeInHierarchy;
                if (visible && loc.VisibilityGroup != null)
                    visible = loc.VisibilityGroup.alpha > 0.05f;
            }
            catch { }
            if (!visible || !alive)
                return; // fog/dead: leave the last model extrapolating

            var local = mapSurface.InverseTransformPoint(child.position);
            if (_entitySamples.TryGetValue(task.trackEntityId, out var prev)) {
                var dt = now - prev.t;
                if (dt >= 0.5f && dt <= 10f) {
                    var vel = (local - prev.local) / dt;
                    vel.z = 0;
                    task.motionVelLocalPerSec = task.hasMotion
                        ? Vector3.Lerp(task.motionVelLocalPerSec, vel, 0.5f)
                        : vel;
                    _entitySamples[task.trackEntityId] = (local, now);
                }
                else if (dt > 10f) {
                    // stale sample (pause/reload) — restart the fit rather than a wild velocity
                    task.motionVelLocalPerSec = Vector3.zero;
                    _entitySamples[task.trackEntityId] = (local, now);
                }
            }
            else {
                _entitySamples[task.trackEntityId] = (local, now);
                task.motionVelLocalPerSec = Vector3.zero;
            }

            task.motionOriginLocal = local;
            task.motionT0 = now;
            task.hasMotion = true;
            task.trackingLost = false;
            return;
        }
        // entity not found at all — keep extrapolating the last model
    }

    /// <summary>
    /// Extrapolate the aim point to the predicted impact time: now + prep + flight.
    /// Lead displacement is capped and the result clamped to the map envelope.
    /// prepSeconds = the remaining delay before the shot leaves: the full prep estimate
    /// while queued, a short residual during pre-fire/manual-wait corrections.
    /// </summary>
    public void ApplyMotionModel(ArtilleryTask task) => ApplyMotionModel(task, TrackPrepSeconds);

    public void ApplyMotionModel(ArtilleryTask task, float prepSeconds) {
        if (!task.hasMotion || !task.hasAimPoint)
            return;
        var flightSeconds = task.distance > 0.1f ? task.distance / ShellSpeedKmPerSec : 30f;
        var horizon = MissionNow - task.motionT0 + prepSeconds + flightSeconds;
        var lead = task.motionVelLocalPerSec * horizon;
        lead.z = 0;
        if (lead.magnitude > MaxLeadLocalUnits)
            lead = lead.normalized * MaxLeadLocalUnits;
        var aim = task.motionOriginLocal + lead;
        aim.x = Mathf.Clamp(aim.x, MapLocalMinX, MapLocalMaxX);
        aim.y = Mathf.Clamp(aim.y, MapLocalMinY, MapLocalMaxY);
        aim.z = task.aimLocal.z;
        task.aimLocal = aim;
    }

    /// <summary>
    /// Late-bound solution refresh: recompute angel/distance from the task's fixed aim
    /// point and the turret piece's CURRENT position. Called each planning round so a
    /// recalibrated origin corrects every queued task before it reaches a gun.
    /// </summary>
    public void RefreshSolution(ArtilleryTask task) {
        if (!task.hasAimPoint || mapSurface == null)
            return;
        var target = task.aimLocal - GetTurretLocalOnMap();
        var refreshed = BuildMarkTarget(task.aimLocal, target);
        if (Mathf.Abs(Mathf.DeltaAngle(task.angel, refreshed.angel)) > 0.05f
            || Mathf.Abs(task.distance - refreshed.distance) > 0.02f) {
            MelonLogger.Msg(
                $"[FCS] T{task.targetId} solution refreshed: {task.angel:F1}掳/{task.distance:F2}km -> {refreshed.angel:F1}掳/{refreshed.distance:F2}km");
        }
        task.angel = refreshed.angel;
        task.distance = refreshed.distance;
        task.position = refreshed.position;
    }

    /// <summary>
    /// LLM-initiated last-minute re-aim: repoint an existing task at a new static map-local
    /// point. Execution never waits for this — the executor's pre-aim/pre-fire/manual-wait
    /// refresh stages pick the new point up on their next pass (aimAdjusted widens their
    /// gate to static tasks). Clears any motion model: an adjustment is an explicit static
    /// override. A task already on a gun keeps its loaded charge, so a new distance beyond
    /// that charge's reach is rejected (cancel + requeue is the correct path there).
    /// </summary>
    public string AdjustAim(ArtilleryTask task, float localX, float localY, bool onGun) {
        if (mapSurface == null)
            return "map surface unbound";
        var aim = new Vector3(
            Mathf.Clamp(localX, MapLocalMinX, MapLocalMaxX),
            Mathf.Clamp(localY, MapLocalMinY, MapLocalMaxY),
            task.aimLocal.z);
        if (onGun && task.chargeCount > 0) {
            var newDistance = (aim - GetTurretLocalOnMap()).magnitude * 3.8164f;
            var maxRange = task.chargeCount * 5f;
            if (newDistance > maxRange + 0.01f)
                return $"rejected: 新距离{newDistance:F2}km超出已装装药C{task.chargeCount}射程{maxRange:F1}km — 该任务装药已固定, 需cancel后重排";
        }
        task.trackEntityId = "";
        task.hasMotion = false;
        task.trackingLost = false;
        task.hasAimPoint = true;
        task.aimLocal = aim;
        task.aimAdjusted = true;
        RefreshSolution(task);
        MelonLogger.Msg($"[FCS] #{task.serial} (marker T{task.targetId}) aim adjusted by agent -> brg {task.angel:F1}deg, {task.distance:F2}km [{task.progress}]");
        return $"ok: #{task.serial} 已改瞄 -> 方位{task.angel:F1}°, 距离{task.distance:F2}km (当前阶段{task.progress})";
    }

    public IEnumerator GetStableMarkTarget(int index, Action<ArtilleryTask?> completed,
        float timeoutSeconds = MarkerStabilizeTimeoutSeconds) {
        if (turretLocation == null || mapSurface == null) {
            MelonLogger.Error("[FCS] GetStableMarkTarget: TurretLocation or map surface unbound");
            completed(null);
            yield break;
        }

        if (!artilleries.TryGetValue(index, out var artillery)) {
            MelonLogger.Error($"[FCS] GetStableMarkTarget: artillery marker T{index} not found");
            completed(null);
            yield break;
        }

        var deadline = FcsRuntimeClock.Now + Mathf.Max(0.5f, timeoutSeconds);
        var previousRelative = Vector3.zero;
        var havePrevious = false;
        var stableSamples = 0;
        var sampleCount = 0;
        var lastDelta = 0f;

        while (true) {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (FcsRuntimeClock.Now >= deadline)
                break;

            var markerLocal = artillery.localPosition;
            var relative = markerLocal - GetTurretLocalOnMap();
            sampleCount++;

            if (!havePrevious) {
                previousRelative = relative;
                havePrevious = true;
                stableSamples = 1;
            }
            else {
                lastDelta = (relative - previousRelative).magnitude;
                stableSamples = lastDelta <= MarkerStableToleranceLocal
                    ? stableSamples + 1
                    : 1;
                previousRelative = relative;
            }

            if (stableSamples >= MarkerStableSampleCount) {
                var task = BuildMarkTarget(markerLocal, relative);
                MelonLogger.Msg(
                    $"[FCS] T{index} marker stabilized: samples={sampleCount}, drift={lastDelta:F5}, " +
                    $"azimuth={task.angel:F2}°, distance={task.distance:F3}km");
                completed(task);
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(MarkerSampleIntervalSeconds);
        }

        MelonLogger.Warning(
            $"[FCS] T{index} marker did not stabilize within {timeoutSeconds:F1}s; " +
            $"last drift={lastDelta:F5}. Task was not queued; click T{index} again after the map settles.");
        completed(null);
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        for (var i = 0; i < fireMissionRoot.childCount; ++i) {
            var m = fireMissionRoot.GetChild(i).GetComponent<EntityLocation>();
            if (m != null) res.Add(m);
        }
        return res;
    }
    
}
