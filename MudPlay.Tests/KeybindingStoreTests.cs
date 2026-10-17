using Avalonia.Input;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class KeybindingStoreTests
{
    [Fact]
    public void Defaults_AreSeeded_OnFreshStore()
    {
        KeybindingStore store = new();
        Assert.Equal(new KeyChord(Key.C, Alt: true), store.Get(BuiltInAction.OpenConversation));
        Assert.Equal(new KeyChord(Key.F3), store.Get(BuiltInAction.OpenGameDataBrowser));
        Assert.Equal(new KeyChord(Key.S, Ctrl: true), store.Get(BuiltInAction.SaveProfile));
        Assert.Equal(new KeyChord(Key.P, Ctrl: true), store.Get(BuiltInAction.OpenProfileManager));
        // OpenParty ships unbound — F3 now opens the Game Data Browser.
        Assert.Equal(KeyChord.Empty, store.Get(BuiltInAction.OpenParty));
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
    public void FindAction_ReturnsBoundAction_ForKnownChord()
    {
        KeybindingStore store = new();
        Assert.Equal(BuiltInAction.OpenWorkshop, store.FindAction(new KeyChord(Key.F1)));
        Assert.Null(store.FindAction(new KeyChord(Key.F8)));  // unbound by default
    }

    [Fact]
    public void Empty_ChordNeverConflicts()
    {
        KeybindingStore store = new();
        Assert.Null(store.FindAction(KeyChord.Empty));
    }
}
