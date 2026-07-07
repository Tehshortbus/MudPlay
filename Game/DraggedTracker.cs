using System.ComponentModel;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Tracks who — if anyone — is currently dragging the local character while it's
// mortally wounded. A dropped character can't move on its own; a party member
// can `drag <name>` to relocate the body, and the game prints
// "<leader> is dragging you around." to the dragged character on each of the
// dragger's moves. There's no symmetric "stopped dragging" line, so the drag is
// treated as active from the first such line until the character recovers — HP
// back positive clears it, since a standing character drags itself.
//
// Read by the @join / @invite remote handlers so a downed member can tell a
// partymate why it can't join and whether someone's already hauling it out.
public sealed class DraggedTracker : IDisposable
{
    private readonly PlayerState _state;
    private readonly IDisposable _dragSub;
    private bool _disposed;

    // Given name of the player currently dragging us, or null when nobody is —
    // either we're not dropped, or no drag line has arrived since we dropped.
    public string? DraggedBy { get; private set; }

    public DraggedTracker(MessageRouter router, PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _dragSub = router.Subscribe(KnownPatterns.PartyDraggedAround, OnDragged);
        _state.PropertyChanged += OnStateChanged;
    }

    private void OnDragged(MatchResult m)
    {
        // A drag line can only land on a mortally-wounded body — guard so a stray
        // match can't pin a phantom dragger on a healthy character.
        if (!_state.IsMortallyWounded) return;
        if (m.Groups.Count == 0) return;
        string leader = m.Groups[0].Trim();
        if (leader.Length != 0) DraggedBy = leader;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Hp):
            case nameof(PlayerState.MaxHp):
            case nameof(PlayerState.HasPromptData):
                // Recovered (or never really dropped) — nobody's dragging us now.
                if (!_state.IsMortallyWounded) DraggedBy = null;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dragSub.Dispose();
        _state.PropertyChanged -= OnStateChanged;
    }
}
