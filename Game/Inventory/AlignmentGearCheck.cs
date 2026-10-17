using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Asks the game for our alignment when our gear says the recorded one may be wrong.
// On Paradigm that's `pro` (its "EPs:" row is the exact number); on Stock it's
// `who` (Stock's `pro` doesn't show alignment). Either reply updates our recorded
// alignment and the Equipment Manager re-evaluates its blocks against it.
//
// Two kinds of trigger, no timers:
//  • The game says our alignment moved (RequestVerify): gear taken off us ("Your …
//    has been removed."), a wear refused, a victim's forgive, a dark cloud that took
//    us out of Good. The record is what's stale, so this checks whatever the sets say.
//  • A gear set disagrees with the record (RequestCheck): an item blocked on
//    alignment alone, or alignment-gated gear with no alignment known yet — raised
//    when a block appears, sets are edited, or a profile loads. Checks only while
//    the disagreement is there.
// One check at most a minute: the game strips gear one line per item.
//
// Checks only go out on a prompt, so nothing is sent before we're in the game.
public sealed class AlignmentGearCheck
{
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(1);

    private readonly Func<bool> _needsCheck;
    private readonly Func<string> _verifyCommand;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private bool _checkPending;
    private bool _verifyPending;

    // needsCheck: a set disagrees with our recorded alignment. verifyCommand: what
    // asks the game for our alignment (`pro` / `who`).
    public AlignmentGearCheck(Func<bool> needsCheck, Func<string>? verifyCommand = null,
        LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(needsCheck);
        _needsCheck = needsCheck;
        _verifyCommand = verifyCommand ?? (static () => "who");
        _log = log;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // A block appeared, sets were edited, a profile loaded: check if the sets now
    // disagree with the record.
    public void RequestCheck() => _checkPending = true;

    // The game says our alignment moved: check it, sets or no sets.
    public void RequestVerify() => _verifyPending = true;

    // The master switch (true = off): off, the check stays pending and goes out
    // at the first prompt after the switch is back on.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Every prompt: send what's pending once the gap has passed.
    public void OnPrompt()
    {
        if (!_checkPending && !_verifyPending) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        DateTimeOffset now = _now();
        if (now - _lastSent < Gap) return;
        bool verify = _verifyPending;
        _checkPending = _verifyPending = false;
        if (!verify && !_needsCheck()) return;
        string command = _verifyCommand();
        _lastSent = now;
        _wire.Send(command);
        _log?.Info("Equipment", verify
            ? $"the game says our alignment moved — sent `{command}` to learn where it is"
            : $"gear set disagrees with our recorded alignment — sent `{command}` to verify it");
    }
}
