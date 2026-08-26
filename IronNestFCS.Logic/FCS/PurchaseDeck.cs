using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;
using System.Text.RegularExpressions;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private const float SyncIntervalSeconds = 0.25f;
    private const int StableSamplesRequired = 3;

    /// <summary>Requisition slot the game accepts a dragged punchcard at.</summary>
    private static readonly Vector3 CardSlotPosition = new(6.4814f, -2.4675f, -22.0968f);

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
    /// Normalizes a physical punchcard ID into the spelling used by the BulletType enum and by
    /// external card requests. The three replacements run in this fixed order, each replaces every
    /// occurrence (not just a suffix), and all three are case sensitive even though the ID
    /// comparisons around them are OrdinalIgnoreCase.
    ///
    /// Quirks it exists for: the game ships "SMOKE" where the enum spells SMK, and "PCLM" where the
    /// upstream enum member is spelled PLCM. This is the only normalization in the codebase - the
    /// availability scan and every purchase path share it, otherwise a shell could be scanned under
    /// one name and never be buyable under that same name.
    /// </summary>
    internal static string NormalizeCardId(string id) =>
        id.Replace("SMOKE", "SMK").Replace("PCLM", "PLCM").Replace("Shell", "").Trim();

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

            // Shares NormalizeCardId with the purchase paths. Its trailing Trim() is what lets a card
            // id carrying stray whitespace parse here at all - without it that shell would be missing
            // from bulletCards and BuyShell would never find a card for it.
            if (TryParse(NormalizeCardId(id), out BulletType type)) {
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

    /// <summary>
    /// Physical core shared by every purchase path: focus, park the card on the requisition slot and
    /// let the game's drag logic snap it in, then settle and re-check focus before anything is clicked.
    /// </summary>
    private IEnumerator InsertCard(Transform card) {
        yield return FcsRuntimeClock.WaitUntilFocused();
        card.position = CardSlotPosition;
        // Null-conditional on purpose: a card object without the drag component is skipped silently
        // instead of throwing out of the purchase coroutine.
        card.GetComponent<DraggableItem>()?.MoveToSlot();
        yield return FcsRuntimeClock.WaitForSeconds(0.5f);
        yield return FcsRuntimeClock.WaitUntilFocused();
    }

    /// <summary>Presses the universal buy button and waits for the requisition animation to finish.</summary>
    private IEnumerator PressBuy() {
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return FcsRuntimeClock.WaitForSeconds(2f);
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = bulletCards.GetValueOrDefault(type);
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card in current physical requisition state");
            yield break;
        }

        yield return InsertCard(card);

        switch (leftRight) {
            case LeftRight.Left:
                GetLeftRightDial().SetDialValue(0);
                break;
            case LeftRight.Right:
                GetLeftRightDial().SetDialValue(1);
                break;
        }
        yield return PressBuy();
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card in current physical requisition state");
            yield break;
        }

        // Deliberate behaviour change: powders now take the same physical core as every other card,
        // which inserts one extra focus gate between the settle wait and the buy click.
        yield return InsertCard(_powderCard);
        yield return PressBuy();
    }

    /// <summary>
    /// Legacy signature kept for callers that have no distance dial value; forwards with distance unset.
    /// </summary>
    public IEnumerator BuyCardById(string cardId, float? bearingDeg, string? startGrid, Action<string> done)
        => BuyCardById(cardId, bearingDeg, null, startGrid, done);

    /// <summary>
    /// Buys one arbitrary punchcard by its game ID, optionally dialling in the recon bearing/distance
    /// and the start grid that appear on the console only after the card is inserted.
    /// The caller must already hold the requisition lock. Every exit reports through <paramref name="done"/>.
    /// </summary>
    public IEnumerator BuyCardById(
        string cardId,
        float? bearingDeg,
        float? distanceKm,
        string? startGrid,
        Action<string> done) {
        if (_requisitionConsole == null) {
            done("requisition console unbound");
            yield break;
        }

        // Scan every punchcard, inactive ones included. Card ids go into the "available" echo in their
        // raw form and in traversal order (no dedup); normalization is applied to the deck side only -
        // the requested cardId is never normalized or trimmed, so callers can address a raw game id too.
        Transform? card = null;
        var available = new List<string>();
        foreach (var runtime in _requisitionConsole.GetComponentsInChildren<PunchcardRuntime>(true)) {
            string? id = null;
            try { id = runtime.CurrentDefinition?.ID; }
            catch { }

            if (string.IsNullOrWhiteSpace(id))
                continue;

            available.Add(id!);
            // No break on a hit: with duplicate names the last match wins.
            if (string.Equals(id, cardId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeCardId(id!), cardId, StringComparison.OrdinalIgnoreCase))
                card = runtime.transform;
        }

        if (card == null) {
            done($"card '{cardId}' not found; available [{string.Join(", ", available)}]");
            yield break;
        }

        if (card.GetComponent<DraggableItem>() == null) {
            done("card has no DraggableItem");
            yield break;
        }

        yield return InsertCard(card);

        if (bearingDeg is { } bearing) {
            // The recon controls are spawned dynamically once the card is accepted, so this lookup is
            // scene-global: rooting it at the requisition console would never find them. The deadline
            // runs on unscaled realtime while the step wait runs on the task clock, which stalls while
            // the game is unfocused or paused.
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

            // Unity lifetime comparison, not "?.": on a destroyed-but-non-null dial "?." would call into
            // a dead object and throw MissingReferenceException out of this coroutine, so "done" would
            // never fire. A missing dial just skips the physical knob and leaves the readback plus the
            // internal setter to do the work.
            if (bridge.bearingDial != null)
                bridge.bearingDial.SetDialValue(bearing);
            yield return FcsRuntimeClock.WaitForSeconds(0.3f);

            var applied = float.NaN;
            try { applied = bridge.Bearing; }
            catch { }

            // Wrap-safe comparison: a requested 359 read back as -1 is not an error. A failed read (NaN)
            // takes the same compensation path as an out-of-tolerance one.
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

            if (distanceKm is { } distance) {
                if (bridge.distanceDial != null)
                    bridge.distanceDial.SetDialValue(distance);
                yield return FcsRuntimeClock.WaitForSeconds(0.3f);

                var appliedDistance = float.NaN;
                try { appliedDistance = bridge.Distance; }
                catch { }

                // Plain difference here - distance does not wrap.
                if (float.IsNaN(appliedDistance) || Mathf.Abs(appliedDistance - distance) > 0.05f) {
                    try {
                        bridge.SetDistanceInternal(distance, true);
                        bridge.ForceRefreshAll();
                        appliedDistance = bridge.Distance;
                    }
                    catch { }
                }

                MelonLogger.Msg($"[FCS] card distance requested {distance:F1} applied {appliedDistance:F1}");
                yield return FcsRuntimeClock.WaitForSeconds(0.3f);
            }
        }
        else if (distanceKm != null) {
            done("distanceKm given without bearingDeg — the distance dial lives on the bearing console controls (give both)");
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(startGrid)) {
            // The pattern is matched against the trimmed input so " P4 " is accepted, but every echo
            // below reports the original untrimmed string.
            var match = Regex.Match(startGrid!.Trim(), @"^([A-Za-z])\s*(\d{1,2})$");
            if (!match.Success) {
                done($"cannot parse startGrid '{startGrid}' (expected like 'P4')");
                yield break;
            }

            // Scene-global again, active objects only. Exclusive else-if, and a later binder overwrites
            // an earlier one; a parent named for both lanes counts as the letter dial.
            DialToSplitFlipDisplayBinder? letterBinder = null;
            DialToSplitFlipDisplayBinder? numberBinder = null;
            foreach (var binder in UnityEngine.Object.FindObjectsOfType<DialToSplitFlipDisplayBinder>()) {
                var parent = binder.transform.parent;
                var parentName = parent != null ? parent.name : "";
                if (parentName.Contains("Location L"))
                    letterBinder = binder;
                else if (parentName.Contains("Location N"))
                    numberBinder = binder;
            }

            if (letterBinder == null || numberBinder == null) {
                done("start-grid dials not found (card may not support a start position)");
                yield break;
            }

            var letterResult = SetFlapDialSymbol(letterBinder, match.Groups[1].Value.ToUpperInvariant());
            var numberResult = SetFlapDialSymbol(numberBinder, match.Groups[2].Value);
            // Logged before the verdict, so both the successful and the failed attempt leave a trace.
            MelonLogger.Msg($"[FCS] card start grid '{startGrid}': letter={letterResult}, number={numberResult}");
            if (letterResult != "ok" || numberResult != "ok") {
                done($"start grid failed: letter={letterResult}, number={numberResult}");
                yield break;
            }

            yield return FcsRuntimeClock.WaitForSeconds(0.4f);
        }

        yield return PressBuy();
        done("ok");
    }

    /// <summary>
    /// Drives one split-flap dial to a symbol. The lookup is a substring search over the whole symbol
    /// table, so a two-digit grid number resolves to the position of its first digit rather than failing.
    /// </summary>
    private static string SetFlapDialSymbol(DialToSplitFlipDisplayBinder binder, string symbol) {
        var symbols = binder.orderedSymbols ?? "";
        var index = symbols.IndexOf(symbol, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return $"symbol '{symbol}' not in [{symbols}]";

        var min = binder.outputRangeMin;
        var max = binder.outputRangeMax;
        var value = symbols.Length > 1
            ? min + (max - min) * index / (symbols.Length - 1f)
            : min;

        // The binder's own value->index mapping is the authority, so nudge towards the target instead of
        // trusting the linear inverse. Both the loop condition and the direction re-read the mapping every
        // round: the mapping is not guaranteed monotone at the boundaries. Not converging within five
        // rounds is not an error - the dial is written and reported "ok" either way.
        for (var attempt = 0; attempt < 5 && binder.MapDialValueToSymbolIndex(value) != index; attempt++)
            value += (max - min) / (symbols.Length * 4f)
                   * (binder.MapDialValueToSymbolIndex(value) < index ? 1f : -1f);

        binder.dial?.SetDialValue(value);
        return "ok";
    }

}
