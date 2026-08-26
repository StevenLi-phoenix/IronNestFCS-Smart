using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    /// <summary>
    /// 玩家可拖动的炮塔棋子（map surface 下的子物体）名。射击原点即指挥官对自己炮位的置信，
    /// 摆错棋子就得到错的诸元——by design。绑定日志的对象名由该常量插值生成。
    /// </summary>
    public const string PlayerTurretPieceName = "Player Turret Piece";

    // 地图有效包络在 km 系是 x ∈ [-1, 27]、y ∈ [-1, 16]，但所有「夹进地图包络」的运算
    // （ApplyMotionModel 的 aim、AdjustAim 的入参 x/y）一律在局部单位下夹、不先转 km。
    // 因此把包络预先按 local = (km − offset) / 3.8164 换算，并以字面表达式书写而非十进制
    // 近似值，避免与运行期换算之间出现舍入分岔。
    public const float MapLocalMinX = (-1f - 10.016f) / 3.8164f;
    public const float MapLocalMaxX = (27f - 10.016f) / 3.8164f;
    public const float MapLocalMinY = (-1f - 5.235f) / 3.8164f;
    public const float MapLocalMaxY = (16f - 5.235f) / 3.8164f;

    private const float MarkerSampleIntervalSeconds = 0.1f;
    private const float MarkerStabilizeTimeoutSeconds = 2f;
    private const float MarkerStableToleranceLocal = 0.0025f;
    private const int MarkerStableSampleCount = 3;

    // 运动目标模型常量。
    private const float TrackingLostAfterSeconds = 90f;
    private const float MinSampleIntervalSeconds = 0.5f;
    private const float MaxSampleIntervalSeconds = 10f;
    private const float VelocityLowPassFactor = 0.5f;
    private const float MaxLeadLocal = 3f / 3.8164f; // 提前量上限 3km，局部单位。
    private const float DefaultPrepSeconds = 45f;
    private const float FallbackShellSpeedKmPerSecond = 0.4f;
    private const float FallbackFlightSeconds = 30f;
    private const float VisibleAlphaThreshold = 0.05f;

    // 诸元刷新只在超阈时打日志；三个字段本身无条件覆写。
    private const float SolutionLogAngleEpsilonDegrees = 0.05f;
    private const float SolutionLogDistanceEpsilonKm = 0.02f;

    // 标记移动的最小位移平方，避免每 0.5s 对同一落点反复写 transform。
    private const float MarkerMoveEpsilonSquared = 1e-6f;

    private Transform? turretLocation;
    private Transform? mapSurface;
    private Transform? turretMapModel;
    private Dictionary<int, Transform> artilleries = new();
    private Transform? fireMissionRoot;
    private FireMission? fireMission;

    // 被跟踪实体的上一次采样。键是 trackEntityId 而**不是**任务 serial：跟踪同一实体的多个
    // 任务共享同一份样本，0.5–10s 采样窗与 0.5 低通才是对「实体」而不是对「任务」生效的。
    // 字典从不清理（无 Reset、换场景不清空），只随 MapTable 实例消亡。
    private readonly Dictionary<string, (Vector3 local, float t)> _entitySamples = new();

    // 24h 世界时钟。昂贵查找必须缓存；缓存仍为 null 时每次求值重扫是规范行为（见 MissionNow）。
    private static GenericTimerSceneSync? _worldClock;

    /// <summary>
    /// 任务时钟：运动模型的 t0 / horizon 一律以它为基准。
    /// 优先场景里的 24h 世界时钟；其次任务秒表；再退实时时钟。
    ///
    /// 三个分支的取舍都是可观测的：世界时钟读数 ≤ 0（对象存在但尚未启动）只是跳过该分支
    /// 下落到秒表，**不清缓存、不重新扫描**；只有 try 块访问抛异常才清缓存以触发下次重扫。
    /// 缓存为 null 时每次求值都重扫一遍——这是「禁止每帧 FindObjectsOfType」的具名例外：
    /// 加一个「已扫描过」标志会让晚于首次调用才生成的世界时钟**永远**不被拾取，MissionNow
    /// 永久退到另一个时间基准，模型的 t0/horizon 全体改变。
    /// </summary>
    public static float MissionNow {
        get {
            try {
                if (_worldClock == null) {
                    var clocks = UnityEngine.Object.FindObjectsOfType<GenericTimerSceneSync>();
                    GenericTimerSceneSync? best = null;
                    if (clocks != null) {
                        for (var i = 0; i < clocks.Length; i++) {
                            var clock = clocks[i];
                            if (clock == null) continue;
                            if (best == null || clock.CurrentTime > best.CurrentTime)
                                best = clock;
                        }
                    }
                    _worldClock = best;
                }

                if (_worldClock != null && _worldClock.CurrentTime > 0f)
                    return _worldClock.CurrentTime;
            }
            catch {
                _worldClock = null;
            }

            try {
                var tracker = MissionStatsTracker.Instance;
                if (tracker != null && tracker.timerRunning)
                    return tracker.timerValue;
            }
            catch { }

            return Time.realtimeSinceStartup;
        }
    }

    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        turretLocation = null;
        mapSurface = null;
        turretMapModel = null;
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
            position = artilleryLocalPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f),
            // 固化瞄点：有它才能在每个规划轮以当前射击原点重推诸元（晚绑定解算），
            // 也才能被 T9/T10 标记循环画出来。
            hasAimPoint = true,
            aimLocal = artilleryLocalPosition
        };
    }

    /// <summary>
    /// 射击原点（map surface 局部坐标，完整 Vector3 含 z）。
    ///
    /// 每次调用都做一次惰性重试：棋子可能晚于场景绑定才生成，场景重载也会让缓存的 Transform
    /// 失效。实现里刻意**没有**「已打印过」标志——每次惰性 Find 成功都打印一次绑定行，
    /// 这样重绑定是可观测的；Find 失败不打印。
    /// </summary>
    public Vector3 GetTurretLocalOnMap() {
        if (turretMapModel == null && mapSurface != null) {
            turretMapModel = mapSurface.Find(PlayerTurretPieceName);
            if (turretMapModel != null) {
                var bound = turretMapModel.localPosition;
                MelonLogger.Msg(
                    $"[FCS] firing origin bound to '{PlayerTurretPieceName}' local=({bound.x:F3},{bound.y:F3})");
            }
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
        return BuildMarkTarget(artillery.localPosition, target);
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

    /// <summary>
    /// 以当前射击原点重推任务的 angel/distance/position（晚绑定解算）。
    ///
    /// 入口守卫里的 mapSurface 判空不可省：未绑定时 GetTurretLocalOnMap() 返回 Vector3.zero，
    /// 会把全部待办任务的诸元覆写成「以地图原点为炮位」的垃圾解。此处静默返回、不动任何字段。
    /// </summary>
    public void RefreshSolution(ArtilleryTask task) {
        if (!task.hasAimPoint || mapSurface == null)
            return;

        var origin = GetTurretLocalOnMap();
        var refreshed = BuildMarkTarget(task.aimLocal, task.aimLocal - origin);

        var previousAngel = task.angel;
        var previousDistance = task.distance;

        task.angel = refreshed.angel;
        task.distance = refreshed.distance;
        task.position = refreshed.position;

        // 方位比较必须环绕安全：按 |a − b| 判会在跨 0°/360° 时得到 ~360 的假差值，每轮规划刷日志。
        if (Mathf.Abs(Mathf.DeltaAngle(previousAngel, refreshed.angel)) > SolutionLogAngleEpsilonDegrees
            || Mathf.Abs(previousDistance - refreshed.distance) > SolutionLogDistanceEpsilonKm) {
            MelonLogger.Msg(
                $"[FCS] #{task.serial} solution refreshed: {previousAngel:F1}°/{previousDistance:F2}km -> " +
                $"{refreshed.angel:F1}°/{refreshed.distance:F2}km");
        }
    }

    /// <summary>
    /// 采样被跟踪实体并拟合线性运动模型。
    ///
    /// 战争迷雾语义：不可见、已死、或整棵 fire-mission root 里根本没找到该实体时，一律静默返回
    /// 并**保留旧模型继续外推**——三条路径行为一致但成因不同。
    /// </summary>
    public void UpdateEntityMotion(ArtilleryTask task) {
        if (task.trackEntityId.Length == 0 || fireMissionRoot == null || mapSurface == null)
            return;

        var now = MissionNow;

        // 赋值而不是条件更新：没有模型时 trackingLost 必须被显式清成 false。
        task.trackingLost = task.hasMotion && now - task.motionT0 > TrackingLostAfterSeconds;

        // 只遍历直接子物体（不递归）：采样点必须是子物体本身的 transform，改用
        // GetComponentsInChildren 会命中孙层组件并采出不同的局部坐标。
        for (var i = 0; i < fireMissionRoot.childCount; i++) {
            var child = fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;

            // 逐级跳过：读 Entity 与读 ID/RawID 的异常都只跳过这一个子物体，扫描继续。
            // 把它们做成「保留旧模型 return」会让一个坏子物体永久遮蔽真正的被跟踪实体。
            MapEntity? entity = null;
            try { entity = loc.Entity; }
            catch { }
            if (entity == null) continue;

            // ID 与 RawID 共用同一个 try：ID 抛异常时 rawId 保持 null。
            string? id = null, rawId = null;
            try {
                id = entity.ID;
                rawId = entity.RawID;
            }
            catch { }

            // 序数、大小写敏感比较（卡片 id 那套 OrdinalIgnoreCase 不适用于实体 id）。
            if (id != task.trackEntityId && rawId != task.trackEntityId) continue;

            // 命中即止：从这里开始的每条路径都结束整个方法，不再看后续子物体。
            //
            // 三步求值必须在同一个 try 内且顺序固定，兜底取值 visible=false / alive=true 使
            // 「异常 ≡ 进入迷雾」。拆成两个 try 或把 visible 提到 alive 之前，IsAlive 抛异常时
            // 会得到 alive=true 且 visible=true，于是继续采样刷新模型——与规范相反。
            bool visible = false, alive = true;
            try {
                alive = entity.IsAlive;
                visible = loc.VisualRoot != null && loc.VisualRoot.activeInHierarchy;
                if (visible && loc.VisibilityGroup != null)
                    visible = loc.VisibilityGroup.alpha > VisibleAlphaThreshold;
            }
            catch { }

            if (!visible || !alive) return;

            var local = mapSurface.InverseTransformPoint(child.position);

            if (_entitySamples.TryGetValue(task.trackEntityId, out var previous)) {
                var dt = now - previous.t;
                if (dt >= MinSampleIntervalSeconds && dt <= MaxSampleIntervalSeconds) {
                    var sampled = (local - previous.local) / dt;
                    sampled.z = 0f;
                    task.motionVelLocalPerSec = task.hasMotion
                        ? Vector3.Lerp(task.motionVelLocalPerSec, sampled, VelocityLowPassFactor)
                        : sampled;
                    _entitySamples[task.trackEntityId] = (local, now);
                }
                else if (dt > MaxSampleIntervalSeconds) {
                    // 暂停/读档留下的陈旧样本：归零重新拟合。
                    task.motionVelLocalPerSec = Vector3.zero;
                    _entitySamples[task.trackEntityId] = (local, now);
                }
                // dt < 0.5s（含时钟回拨造成的负 dt）：速度与样本**都不动**，下一次调用仍以旧样本
                // 计 dt——这正是 0.5s 最小采样窗真正生效的机制。顺手更新样本会把规划轮 + 执行期
                // 3s 重调的高频调用拟合成噪声。
            }
            else {
                task.motionVelLocalPerSec = Vector3.zero;
                _entitySamples[task.trackEntityId] = (local, now);
            }

            // 四个分支之后无条件执行。样本时间戳与 motionT0 是两个独立时间基准，不可合并。
            task.motionOriginLocal = local;
            task.motionT0 = now;
            task.hasMotion = true;
            task.trackingLost = false;
            return;
        }
    }

    public void ApplyMotionModel(ArtilleryTask task) => ApplyMotionModel(task, DefaultPrepSeconds);

    /// <summary>
    /// 按线性运动模型把瞄点推到预测落点。
    ///
    /// 两遍不动点迭代：提前量改变射程、射程又改变飞行时间。本方法**不**更新
    /// angel/distance/position，调用方必须紧跟 RefreshSolution。
    /// </summary>
    public void ApplyMotionModel(ArtilleryTask task, float prepSeconds) {
        if (!task.hasMotion || !task.hasAimPoint)
            return;

        var aim = task.aimLocal;
        // 初值是任务上一次解算出的射程，不是现场从 aimLocal 重推的。
        var distanceKm = task.distance;

        for (var pass = 0; pass < 2; pass++) {
            var horizon = MissionNow - task.motionT0 + prepSeconds + FlightSecondsFor(task, distanceKm);

            var lead = task.motionVelLocalPerSec * horizon;
            lead.z = 0f;
            lead = Vector3.ClampMagnitude(lead, MaxLeadLocal);

            aim = task.motionOriginLocal + lead;
            aim.x = Mathf.Clamp(aim.x, MapLocalMinX, MapLocalMaxX);
            aim.y = Mathf.Clamp(aim.y, MapLocalMinY, MapLocalMaxY);
            // task.aimLocal 直到两遍全部结束才写回，所以遍内取到的始终是原始 z。
            aim.z = task.aimLocal.z;

            // 遍末用水平口径重算射程供下一遍。
            var toAim = aim - GetTurretLocalOnMap();
            distanceKm = new Vector2(toAim.x, toAim.y).magnitude * 3.8164f;
        }

        task.aimLocal = aim;
    }

    /// <summary>
    /// 该射程下的飞行时间估计。装药未定时按最小可行装药估。
    ///
    /// 必须查实测 TTI 表：扁平 0.4km/s 平均弹速曾把 C1/C2 的飞行时间低估近一倍，是「炮弹落在
    /// 移动目标屁股后面」的系统性根因；0.4 只作兜底。
    /// </summary>
    private static float FlightSecondsFor(ArtilleryTask task, float distanceKm) {
        var charge = task.chargeCount is >= 1 and <= 6
            ? task.chargeCount
            : Mathf.Clamp(Mathf.CeilToInt(distanceKm / 5f), 1, 6);

        if (TimeToImpactEstimator.TryEstimateSeconds(distanceKm, charge, out var seconds))
            return seconds;

        if (distanceKm > 0.1f)
            return distanceKm / FallbackShellSpeedKmPerSecond;

        return FallbackFlightSeconds;
    }

    /// <summary>
    /// 沿「射击原点 → aimLocal」方向缩短到 rangeKm 的瞄点（清膛倾泻弹用）。
    /// 一律水平口径，且结果**不夹**地图包络。
    /// </summary>
    public Vector3 ShortenedAim(ArtilleryTask task, float rangeKm) {
        var dir = task.aimLocal - GetTurretLocalOnMap();
        dir.z = 0f;

        var lenKm = new Vector2(dir.x, dir.y).magnitude * 3.8164f;
        if (lenKm < 0.01f)
            return task.aimLocal;

        // dir 保持局部单位，rangeKm / lenKm 是无量纲比例。
        var aim = GetTurretLocalOnMap() + dir * (rangeKm / lenKm);
        aim.z = task.aimLocal.z;
        return aim;
    }

    /// <summary>
    /// 把 FCS 自己拥有的炮位瞄点标记摆到 aimLocal。
    ///
    /// 方法自身**不做 id 白名单**——传什么 id 就动 artilleries[id]；「只碰 9/10、T1–T8 归玩家」
    /// 完全由调用点 GunTargetMarkerLoop 保证。aimLocal 为 null 时标记不动：击发后它留在原计划
    /// 落点，正是「在途炮弹落在哪」的可视指示，永不归位。
    /// </summary>
    public void SetGunTargetMarker(int id, Vector3? aimLocal) {
        // 该图没有 9/10 号 token，或场景重载后 Il2Cpp 指针失效，都静默返回。
        if (!artilleries.TryGetValue(id, out var marker) || marker == null)
            return;

        if (aimLocal is not { } target)
            return;

        var current = marker.localPosition;
        var moved = new Vector3(target.x, target.y, current.z);
        if ((moved - current).sqrMagnitude > MarkerMoveEpsilonSquared)
            marker.localPosition = moved;
    }

    /// <summary>
    /// agent 改瞄。入参 x/y 是地图局部单位，按局部包络夹取，z 沿用任务原值。
    /// 非阻塞：新点由执行期三阶段在各自下一遍拾取，本方法只改任务数据。
    /// </summary>
    public string AdjustAim(ArtilleryTask task, float x, float y, bool onGun) {
        if (mapSurface == null)
            return "map surface unbound";

        var aim = new Vector3(
            Mathf.Clamp(x, MapLocalMinX, MapLocalMaxX),
            Mathf.Clamp(y, MapLocalMinY, MapLocalMaxY),
            task.aimLocal.z);

        if (onGun && task.chargeCount > 0) {
            // 火药一旦 commit 无法追加，超出已装装药射程就只能 cancel 后重排。
            // 这里的距离口径刻意是含 z 的 Vector3 完整模长，与 ApplyMotionModel/ShortenedAim 的
            // 水平口径不同：炮塔棋子的 localPosition.z 一般与 aimLocal.z 不等，故此处算得的距离
            // 系统性略大于水平距离，边界附近会出现「水平口径通过、三维口径拒绝」。不得为
            // 「一致性」改成水平口径。
            var newDistance = (aim - GetTurretLocalOnMap()).magnitude * 3.8164f;
            var maxRange = task.chargeCount * 5f;
            if (newDistance > maxRange + 0.01f) {
                return $"rejected: 新距离{newDistance:F2}km超出已装装药C{task.chargeCount}射程{maxRange:F1}km" +
                       " — 该任务装药已固定, 需cancel后重排";
            }
        }

        // 改瞄是显式的静态覆盖：清掉运动模型与跟踪，之后不再自动外推。
        task.trackEntityId = "";
        task.hasMotion = false;
        task.trackingLost = false;

        task.hasAimPoint = true;
        task.aimLocal = aim;
        // 让执行期各刷新阶段的三联门对这个静态任务也打开。
        task.aimAdjusted = true;

        RefreshSolution(task);

        // 日志在 RefreshSolution 之后打印，方位/距离用刷新后的值。
        MelonLogger.Msg(
            $"[FCS] #{task.serial} (marker #{task.serial}) aim adjusted by agent -> " +
            $"brg {task.angel:F1}deg, {task.distance:F2}km [{task.progress}]");

        return $"ok: #{task.serial} 已改瞄 -> 方位{task.angel:F1}°, 距离{task.distance:F2}km (当前阶段{task.progress})";
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
