using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Modeless floating Party window VM. Binds directly to PartyState's
// observable collection so member additions / removals / per-member HP/MA
// updates flow through to the UI without the VM having to maintain its own
// mirror.
//
// Per-row surface: leader-star (IsLeader), rank-chip (IsSelf only — see
// LocalRank), name + class, HP bar + numeric, MA bar + numeric, status-flag
// chips, per-row Uninvite button.
//
// Uninvite: emits uninvite <name> on the wire when the local character is
// the party leader. Non-leader rows render the button disabled (in-game
// command would no-op anyway).
public sealed partial class PartyViewModel : ObservableObject, IDisposable
{
    private readonly Action<byte[]>? _wireSender;
    private readonly ProfileService? _profile;
    private bool _disposed;

    public PartyState State { get; }

    // The PartyWindow's title: the window's menu name, then this character — its
    // given name and HP percent ("Party — Cidir (100%)") — so each client's window
    // says whose it is. Every client in a party showed the leader's name (report:
    // Cidir's window read "Nineteen (95%)"). Our own row is in the roster in a party
    // and solo alike; until it arrives, the member count stands in ("Party (N)").
    // Recomputes on membership churn, a party-state change, and a member's HP /
    // name / self flag changing (per-member sub).
    public string HeaderText
    {
        get
        {
            if (State.Members.Count == 0) return "Party — no party active";
            PartyMember? self = State.Members.FirstOrDefault(m => m.IsSelf);
            if (self is null || string.IsNullOrEmpty(self.Name))
                return $"Party ({State.Members.Count})";
            // Given name only — the form MajorMUD itself uses when addressing a player.
            int space = self.Name.IndexOf(' ');
            string given = space >= 0 ? self.Name[..space] : self.Name;
            return $"Party — {given} ({self.HpPercent}%)";
        }
    }

    // True when the roster has any row to show — drives the member-list visibility
    // (and hides the empty hint). A lone self row while solo counts, so the window
    // shows the local character out of a party, not just when IsInParty.
    public bool HasRows => State.Members.Count > 0;

    // Local character's persisted rank (Front / Mid / Back). Read from the
    // loaded profile's "Party" settings on construction and on every
    // ProfileService.ProfileMutated tick so the Settings → Party Apply path
    // reflects immediately in the PartyWindow. Drives the rank-chip rendered
    // on the local (IsSelf) row only — other party members' rank isn't
    // disclosed by par output.
    [ObservableProperty] private PartyRank _localRank = PartyRank.Mid;

    public PartyViewModel(PartyState state, Action<byte[]>? wireSender = null)
        : this(state, wireSender, AppServices.Current.Profile)
    {
    }

    // Full-control constructor. A null profile skips the
    // ProfileService.ProfileLoaded / ProfileMutated subscriptions — LocalRank
    // then stays at its PartyRank.Mid default. Production code uses the
    // two-arg overload above; that path grabs the live AppServices.Profile.
    public PartyViewModel(PartyState state, Action<byte[]>? wireSender, ProfileService? profile)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _wireSender = wireSender;
        _profile = profile;

        // Membership churn → refresh HeaderText AND adjust per-member
        // PropertyChanged subscriptions so our own HP changes refresh
        // the header live (header reads "Party — {SelfGiven} ({SelfHpPercent}%)").
        // Named handlers (not lambdas) so Dispose can detach them — PartyState
        // outlives this VM, so a lambda sub would pin the VM for the app's life.
        State.Members.CollectionChanged += OnMembersChanged;
        // Existing members at construction time also need a sub —
        // CollectionChanged only fires for subsequent additions.
        foreach (PartyMember m in State.Members)
            m.PropertyChanged += OnMemberPropertyChanged;
        State.PropertyChanged += OnStatePropertyChanged;

        if (_profile is not null)
        {
            _profile.ProfileLoaded += OnProfileLoaded;
            _profile.ProfileMutated += OnProfileMutated;
            _profile.ProfileClosed += OnProfileClosed;
            RefreshFromPartySettings();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Detach every subscription to app-lifetime PartyState / its members,
        // else this VM (and the closed PartyWindow it backs) can't be collected.
        State.Members.CollectionChanged -= OnMembersChanged;
        foreach (PartyMember m in State.Members)
            m.PropertyChanged -= OnMemberPropertyChanged;
        State.PropertyChanged -= OnStatePropertyChanged;
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= OnProfileLoaded;
            _profile.ProfileMutated -= OnProfileMutated;
            _profile.ProfileClosed -= OnProfileClosed;
        }
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (object? o in e.OldItems)
                if (o is PartyMember m) m.PropertyChanged -= OnMemberPropertyChanged;
        if (e.NewItems is not null)
            foreach (object? o in e.NewItems)
                if (o is PartyMember m) m.PropertyChanged += OnMemberPropertyChanged;
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HasRows));
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(HeaderText));

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the fields that contribute to HeaderText warrant a
        // refresh — other PartyMember properties churn frequently
        // (HpRichDisplay, status flags) and don't affect the header.
        if (e.PropertyName is nameof(PartyMember.HpPercent)
                          or nameof(PartyMember.IsSelf)
                          or nameof(PartyMember.Name))
        {
            OnPropertyChanged(nameof(HeaderText));
        }
    }

    private void OnProfileLoaded(CharacterProfile _) => RefreshFromPartySettings();
    private void OnProfileMutated(CharacterProfile _) => RefreshFromPartySettings();
    private void OnProfileClosed() => LocalRank = PartyRank.Mid;

    private void RefreshFromPartySettings() => LocalRank = ReadPartySettings().Rank;

    private PartySettings ReadPartySettings()
    {
        if (_profile?.Current is not { } profile
            || profile.Settings is null
            || !profile.Settings.TryGetValue("Party", out JsonElement json))
            return new PartySettings();
        try { return JsonSerializer.Deserialize<PartySettings>(json) ?? new PartySettings(); }
        catch { return new PartySettings(); }
    }

    // Per-row Uninvite. Sends uninvite X on the wire when the local character
    // is the party leader — covers BOTH the withdraw-pending-invite path
    // (PartyManager flips SelfIsLeader true the moment "You have invited X to
    // follow you." fires) and the existing kick-a-real-follower path.
    [RelayCommand]
    private void Uninvite(PartyMember? member)
    {
        if (member is null) return;
        if (string.IsNullOrEmpty(member.Name)) return;
        if (!State.SelfIsLeader) return;
        if (_wireSender is null) return;
        // MajorMUD addresses other players by GIVEN name only.
        int space = member.Name.IndexOf(' ');
        string given = space >= 0 ? member.Name[..space] : member.Name;
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"uninvite {given}\r");
        _wireSender(bytes);
    }
}
