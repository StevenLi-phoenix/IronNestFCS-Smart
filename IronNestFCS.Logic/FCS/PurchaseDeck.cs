using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private const float SyncIntervalSeconds = 0.25f;
    private const int StableSamplesRequired = 3;

    private Transform? _requisitionConsole;
    private Transform? _powderCard;
    private Dictionary<BulletType, Transform> bulletCards = new();
    private HashSet<BulletType> _availableShellTypes = new();
    private LookAtTarget? _buyButton;

    private float _nextSyncAt;
    private string _committedFingerprint = "";
    private string _candidateFingerprint = "";
    private int _candidateSamples;
    private bool _hasCommittedSnapshot;
    private bool _waitingForCardsLogged;

    public IReadOnlyCollection<BulletType> AvailableBulletTypes => _availableShellTypes;

    public bool HasShell(BulletType type) => _availableShellTypes.Contains(type);
    
    public bool TryBind() {
        _requisitionConsole = null;
        _powderCard = null;
        bulletCards.Clear();
        _availableShellTypes.Clear();
        _buyButton = null;
        _nextSyncAt = 0f;
        _committedFingerprint = "";
        _candidateFingerprint = "";
        _candidateSamples = 0;
        _hasCommittedSnapshot = false;
        _waitingForCardsLogged = false;

        var requisitionObject = GameObject.Find("Requisition Console");
        if (requisitionObject == null) {
            MelonLogger.Warning("[FCS] PurchaseDeck: Requisition Console not found");
            return false;
        }

        _requisitionConsole = requisitionObject.transform;
        _buyButton = _requisitionConsole.FindChild("Universal Button").GetComponent<LookAtTarget>();

        // If the physical cards already exist, use them immediately. If the deck is still empty,
        // leave availability uncommitted and let SyncTick pick it up once the game finishes spawning it.
        ScanPhysicalState(commitImmediately: true);
        return true;
    }

    /// <summary>
    /// Polls the real requisition punchcards at low frequency. Physical Transform references are refreshed
    /// on every valid scan; UI availability is only committed after the shell set is stable for several samples.
    /// Returns true only when the committed shell availability changes.
    /// </summary>
    public bool SyncTick() {
        if (_requisitionConsole == null || Time.unscaledTime < _nextSyncAt)
            return false;

        _nextSyncAt = Time.unscaledTime + SyncIntervalSeconds;
        return ScanPhysicalState(commitImmediately: false);
    }

    private bool ScanPhysicalState(bool commitImmediately) {
        if (_requisitionConsole == null)
            return false;

        PunchcardRuntime[] cards;
        try {
            cards = _requisitionConsole.GetComponentsInChildren<PunchcardRuntime>(true);
        }
        catch (Exception ex) {
            MelonLogger.Warning($"[FCS] Requisition sync scan failed: {ex.Message}");
            return false;
        }

        // At mission startup the console exists several seconds before its physical cards do.
        // An empty hierarchy is therefore "not ready", not an authoritative empty ammunition list.
        if (cards.Length == 0) {
            if (!_waitingForCardsLogged) {
                _waitingForCardsLogged = true;
                MelonLogger.Msg("[FCS] Requisition sync: waiting for physical punchcards");
            }
            _candidateFingerprint = "";
            _candidateSamples = 0;
            return false;
        }

        _waitingForCardsLogged = false;

        var nextCards = new Dictionary<BulletType, Transform>();
        Transform? nextPowderCard = null;
        var definitionsComplete = true;

        foreach (var card in cards) {
            string id;
            try {
                var definition = card.CurrentDefinition;
                if (definition == null || string.IsNullOrWhiteSpace(definition.ID)) {
                    definitionsComplete = false;
                    continue;
                }
                id = definition.ID;
            }
            catch {
                definitionsComplete = false;
                continue;
            }

            if (TryParse(
                    id
                        .Replace("SMOKE", "SMK")
                        .Replace("PCLM", "PLCM")
                        .Replace("Shell", ""),
                    out BulletType type
                )) {
                nextCards[type] = card.transform;
            }
            else if (id == "PowderCharges") {
                nextPowderCard = card.transform;
            }
        }

        // Refresh physical object references independently from UI availability. A future game refresh may
        // recreate card objects without changing the shell set.
        bulletCards = nextCards;
        _powderCard = nextPowderCard;

        if (!definitionsComplete) {
            _candidateFingerprint = "";
            _candidateSamples = 0;
            return false;
        }

        var orderedTypes = ((BulletType[])GetValues(typeof(BulletType)))
            .Where(nextCards.ContainsKey)
            .ToArray();
        var fingerprint = string.Join(",", orderedTypes.Select(type => ((int)type).ToString()));

        if (_hasCommittedSnapshot && fingerprint == _committedFingerprint) {
            _candidateFingerprint = "";
            _candidateSamples = 0;
            return false;
        }

        if (commitImmediately) {
            return CommitAvailability(orderedTypes, fingerprint);
        }

        if (fingerprint == _candidateFingerprint) {
            _candidateSamples++;
        }
        else {
            _candidateFingerprint = fingerprint;
            _candidateSamples = 1;
        }

        if (_candidateSamples < StableSamplesRequired)
            return false;

        return CommitAvailability(orderedTypes, fingerprint);
    }

    private bool CommitAvailability(BulletType[] orderedTypes, string fingerprint) {
        var oldTypes = _availableShellTypes;
        var newTypes = orderedTypes.ToHashSet();
        var changed = !_hasCommittedSnapshot || !oldTypes.SetEquals(newTypes);

        _availableShellTypes = newTypes;
        _committedFingerprint = fingerprint;
        _hasCommittedSnapshot = true;
        _candidateFingerprint = "";
        _candidateSamples = 0;

        if (!changed)
            return false;

        var added = orderedTypes.Where(type => !oldTypes.Contains(type)).Select(type => type.DisplayName()).ToArray();
        var removed = oldTypes.Where(type => !newTypes.Contains(type)).Select(type => type.DisplayName()).ToArray();
        var current = orderedTypes.Select(type => type.DisplayName()).ToArray();

        MelonLogger.Msg(
            $"[FCS] Requisition sync: shells={current.Length} [{string.Join(", ", current)}]" +
            (added.Length > 0 ? $" added=[{string.Join(", ", added)}]" : "") +
            (removed.Length > 0 ? $" removed=[{string.Join(", ", removed)}]" : ""));
        return true;
    }
    
    private DialInteractable GetLeftRightDial() {
        var consoleBox = GameObject.Find("Console Box").transform;
        return consoleBox.GetComponentInChildren<DialInteractable>();
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = bulletCards.GetValueOrDefault(type);
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card in current physical requisition state");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        card.position = target;
        card.GetComponent<DraggableItem>().MoveToSlot();
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        
        switch (leftRight) {
            case LeftRight.Left:
                GetLeftRightDial().SetDialValue(0);
                break;
            case LeftRight.Right:
                GetLeftRightDial().SetDialValue(1);
                break;
        }
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return FcsRuntimeClock.WaitForSeconds(2f);
    }

    /// <summary>
    /// Buy any punchcard by id (shell or special card like ScoutPlane). If bearingDeg is
    /// given, waits for the card's spawned console controls and sets the bearing dial,
    /// verifying via the bridge's own value and correcting through its internal setter.
    /// Caller must hold the Requisition lock. Reports the outcome through done().
    /// </summary>
    /// <summary>Drive one grid split-flap dial (letter or number) to a symbol via its binder.</summary>
    private static string SetFlapDialSymbol(DialToSplitFlipDisplayBinder binder, string symbol) {
        var symbols = binder.orderedSymbols ?? "";
        var index = symbols.IndexOf(symbol, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return $"symbol '{symbol}' not in [{symbols}]";
        var min = binder.outputRangeMin;
        var max = binder.outputRangeMax;
        var value = symbols.Length > 1 ? min + (max - min) * index / (symbols.Length - 1) : min;
        // Verify the mapping with the binder's own function and nudge if rounding lands off.
        for (var attempt = 0; attempt < 5 && binder.MapDialValueToSymbolIndex(value) != index; attempt++)
            value += (max - min) / (symbols.Length * 4f) * (binder.MapDialValueToSymbolIndex(value) < index ? 1f : -1f);
        binder.dial?.SetDialValue(value);
        return "ok";
    }

    public IEnumerator BuyCardById(string cardId, float? bearingDeg, string? startGrid, Action<string> done) {
        if (_requisitionConsole == null) {
            done("requisition console unbound");
            yield break;
        }

        Transform? card = null;
        var available = new List<string>();
        foreach (var runtime in _requisitionConsole.GetComponentsInChildren<PunchcardRuntime>(true)) {
            string? id = null;
            try { id = runtime.CurrentDefinition?.ID; } catch { }
            if (string.IsNullOrWhiteSpace(id)) continue;
            available.Add(id!);
            var normalized = id!.Replace("SMOKE", "SMK").Replace("Shell", "").Trim();
            if (string.Equals(id, cardId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, cardId, StringComparison.OrdinalIgnoreCase)) {
                card = runtime.transform;
            }
        }
        if (card == null) {
            done($"card '{cardId}' not found; available [{string.Join(", ", available)}]");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        card.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        var draggable = card.GetComponent<DraggableItem>();
        if (draggable == null) {
            done("card has no DraggableItem");
            yield break;
        }
        draggable.MoveToSlot();
        yield return FcsRuntimeClock.WaitForSeconds(0.6f);

        if (bearingDeg is { } bearing) {
            DialOdometerPunchcardBridge? bridge = null;
            var waitUntil = Time.unscaledTime + 4f;
            while (bridge == null && Time.unscaledTime < waitUntil) {
                bridge = UnityEngine.Object.FindObjectOfType<DialOdometerPunchcardBridge>();
                if (bridge == null)
                    yield return FcsRuntimeClock.WaitForSeconds(0.25f);
            }
            if (bridge == null) {
                done("card accepted but no bearing controls appeared (not a recon card?)");
                yield break;
            }
            if (bridge.bearingDial != null)
                bridge.bearingDial.SetDialValue(bearing);
            yield return FcsRuntimeClock.WaitForSeconds(0.3f);

            var applied = float.NaN;
            try { applied = bridge.Bearing; } catch { }
            if (float.IsNaN(applied) || Mathf.Abs(Mathf.DeltaAngle(applied, bearing)) > 1f) {
                try {
                    bridge.SetBearingInternal(bearing, true);
                    bridge.ForceRefreshAll();
                    applied = bridge.Bearing;
                }
                catch { }
            }
            MelonLogger.Msg($"[FCS] card bearing requested {bearing:F1} applied {applied:F1}");
            yield return FcsRuntimeClock.WaitForSeconds(0.3f);
        }

        if (!string.IsNullOrWhiteSpace(startGrid)) {
            var m = System.Text.RegularExpressions.Regex.Match(startGrid!.Trim(), @"^([A-Za-z])\s*(\d{1,2})$");
            if (!m.Success) {
                done($"cannot parse startGrid '{startGrid}' (expected like 'P4')");
                yield break;
            }
            DialToSplitFlipDisplayBinder? letterBinder = null, numberBinder = null;
            foreach (var binder in UnityEngine.Object.FindObjectsOfType<DialToSplitFlipDisplayBinder>()) {
                var parent = binder.transform.parent;
                var parentName = parent != null ? parent.name : "";
                if (parentName.Contains("Location L")) letterBinder = binder;
                else if (parentName.Contains("Location N")) numberBinder = binder;
            }
            if (letterBinder == null || numberBinder == null) {
                done("start-grid dials not found (card may not support a start position)");
                yield break;
            }
            var letterResult = SetFlapDialSymbol(letterBinder, m.Groups[1].Value.ToUpperInvariant());
            var numberResult = SetFlapDialSymbol(numberBinder, m.Groups[2].Value);
            MelonLogger.Msg($"[FCS] card start grid '{startGrid}': letter={letterResult}, number={numberResult}");
            if (letterResult != "ok" || numberResult != "ok") {
                done($"start grid failed: letter={letterResult}, number={numberResult}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.4f);
        }

        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return FcsRuntimeClock.WaitForSeconds(2f);
        done("ok");
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card in current physical requisition state");
            yield break;
        }

        yield return FcsRuntimeClock.WaitUntilFocused();
        _powderCard.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        _powderCard.GetComponent<DraggableItem>().MoveToSlot();
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return FcsRuntimeClock.WaitForSeconds(2f);
    }
    
}
