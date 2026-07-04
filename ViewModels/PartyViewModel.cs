using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

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

    // Header text shown at the top of the PartyWindow. When a leader is
    // known, shows their given name + current HP percent ("Fujin (94%)");
    // when there's no leader yet (mid-formation, solo, or par hasn't
    // disclosed who leads), falls back to the "Party (N)" count. Recomputes
    // on:
    // - Members.CollectionChanged (membership churn).
    // - State.PropertyChanged (LeaderName flip).
    // - Any member's HpPercent or IsLeader change (per-member sub).
    public string HeaderText
    {
        get
        {
            if (State.Members.Count == 0) return "No party active";
            PartyMember? leader = State.Members.FirstOrDefault(m => m.IsLeader);
            if (leader is null || string.IsNullOrEmpty(leader.Name))
                return $"Party ({State.Members.Count})";
            // Leader name to given only — matches the single-word display
            // and is the form MajorMUD itself uses when addressing the
            // player at most prompts.
            int space = leader.Name.IndexOf(' ');
            string given = space >= 0 ? leader.Name[..space] : leader.Name;
            return $"{given} ({leader.HpPercent}%)";
        }
    }

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

    // Full-control constructor. Pass profile as null from tests to skip the
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
        // PropertyChanged subscriptions so leader-HP changes refresh
        // the header live (header reads "{LeaderGiven} ({LeaderHpPercent}%)").
        State.Members.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (object? o in e.OldItems)
                    if (o is PartyMember m) m.PropertyChanged -= OnMemberPropertyChanged;
            if (e.NewItems is not null)
                foreach (object? o in e.NewItems)
                    if (o is PartyMember m) m.PropertyChanged += OnMemberPropertyChanged;
            OnPropertyChanged(nameof(HeaderText));
        };
        // Existing members at construction time also need a sub —
        // CollectionChanged only fires for subsequent additions.
        foreach (PartyMember m in State.Members)
            m.PropertyChanged += OnMemberPropertyChanged;
        State.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HeaderText));

        if (_profile is not null)
        {
            _profile.ProfileLoaded += OnProfileLoaded;
            _profile.ProfileMutated += OnProfileMutated;
            _profile.ProfileClosed += OnProfileClosed;
            RefreshLocalRank();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= OnProfileLoaded;
            _profile.ProfileMutated -= OnProfileMutated;
            _profile.ProfileClosed -= OnProfileClosed;
        }
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the fields that contribute to HeaderText warrant a
        // refresh — other PartyMember properties churn frequently
        // (HpDisplay, status flags) and don't affect the header.
        if (e.PropertyName is nameof(PartyMember.HpPercent)
                          or nameof(PartyMember.IsLeader)
                          or nameof(PartyMember.Name))
        {
            OnPropertyChanged(nameof(HeaderText));
        }
    }

    private void OnProfileLoaded(CharacterProfile _) => RefreshLocalRank();
    private void OnProfileMutated(CharacterProfile _) => RefreshLocalRank();
    private void OnProfileClosed() => LocalRank = PartyRank.Mid;

    private void RefreshLocalRank()
    {
        if (_profile?.Current is not { } profile)
        {
            LocalRank = PartyRank.Mid;
            return;
        }
        if (profile.Settings is null
            || !profile.Settings.TryGetValue("Party", out JsonElement json))
        {
            LocalRank = PartyRank.Mid;
            return;
        }
        try
        {
            PartySettings dto = JsonSerializer.Deserialize<PartySettings>(json) ?? new PartySettings();
            LocalRank = dto.Rank;
        }
        catch
        {
            LocalRank = PartyRank.Mid;
        }
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
