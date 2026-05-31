using Avalonia.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class KeybindingStoreTests
{
    [Fact]
    public void Defaults_AreSeeded_OnFreshStore()
    {
        KeybindingStore store = new();
        Assert.Equal(new KeyChord(Key.F2), store.Get(BuiltInAction.OpenConversation));
        Assert.Equal(new KeyChord(Key.G, Ctrl: true), store.Get(BuiltInAction.OpenGameDataBrowser));
        Assert.Equal(new KeyChord(Key.S, Ctrl: true, Shift: true), store.Get(BuiltInAction.SaveProfileAs));
    }

    [Fact]
    public void Rebind_ReplacesChord_AndFiresChangeEvent()
    {
        KeybindingStore store = new();
        BuiltInAction? notified = null;
        store.BindingChanged += a => notified = a;

        store.Rebind(BuiltInAction.OpenConversation, new KeyChord(Key.F6, Ctrl: true));

        Assert.Equal(new KeyChord(Key.F6, Ctrl: true), store.Get(BuiltInAction.OpenConversation));
        Assert.Equal(BuiltInAction.OpenConversation, notified);
    }

    [Fact]
    public void IsConflict_FlagsCollidingChord_ReportsCollidingAction()
    {
        KeybindingStore store = new();
        // F2 is bound to OpenConversation by default; rebinding OpenParty
        // to F2 must flag a conflict.
        bool collision = store.IsConflict(new KeyChord(Key.F2), excluding: BuiltInAction.OpenParty,
                                           out BuiltInAction? culprit);
        Assert.True(collision);
        Assert.Equal(BuiltInAction.OpenConversation, culprit);
    }

    [Fact]
    public void IsConflict_ExcludesSpecifiedAction_DuringSelfEdit()
    {
        KeybindingStore store = new();
        // F2 → OpenConversation by default. The OpenConversation editor
        // should NOT flag its own current chord as a collision.
        Assert.False(store.IsConflict(new KeyChord(Key.F2), excluding: BuiltInAction.OpenConversation, out _));
    }

    [Fact]
    public void FindAction_ReturnsBoundAction_ForKnownChord()
    {
        KeybindingStore store = new();
        Assert.Equal(BuiltInAction.OpenWorkshop, store.FindAction(new KeyChord(Key.F4)));
        Assert.Null(store.FindAction(new KeyChord(Key.F8)));  // unbound by default
    }

    [Fact]
    public void Empty_ChordNeverConflicts()
    {
        KeybindingStore store = new();
        Assert.False(store.IsConflict(KeyChord.Empty, excluding: null, out _));
        Assert.Null(store.FindAction(KeyChord.Empty));
    }
}
