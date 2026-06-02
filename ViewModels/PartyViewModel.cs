using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// Modeless floating Party window VM. Binds directly to
/// <see cref="PartyState"/>'s observable collection so member additions /
/// removals / per-member HP/MA updates flow through to the UI without
/// the VM having to maintain its own mirror.
/// </summary>
/// <remarks>
/// <para>
/// Per-row surface: leader-star (IsLeader), rank-chip (IsSelf only — see
/// <see cref="LocalRank"/>), name + class, HP bar + numeric, MA bar +
/// numeric, status-flag chips, per-row Uninvite button.
/// </para>
/// <para>
/// Uninvite: emits <c>uninvite &lt;name&gt;</c> on the wire when the
/// local character is the party leader. Non-leader rows render the
/// button disabled (in-game command would no-op anyway).
/// </para>
/// </remarks>
public sealed partial class PartyViewModel : ObservableObject, IDisposable
{
    private readonly Action<byte[]>? _wireSender;
    private readonly ProfileService? _profile;
    private bool _disposed;

    public PartyState State { get; }

    /// <summary>"Party (N)" header text — recomputes when membership changes.</summary>
    public string HeaderText => $"Party ({State.Members.Count})";

    /// <summary>
    /// Local character's persisted rank (Front / Mid / Back). Read from
    /// the loaded profile's "Party" settings on construction and on every
    /// <see cref="ProfileService.ProfileMutated"/> tick so the Settings →
    /// Party Apply path reflects immediately in the PartyWindow. Drives
    /// the rank-chip rendered on the local (IsSelf) row only — other
    /// party members' rank isn't disclosed by par output.
    /// </summary>
    [ObservableProperty] private PartyRank _localRank = PartyRank.Mid;

    public PartyViewModel(PartyState state, Action<byte[]>? wireSender = null)
        : this(state, wireSender, AppServices.Current.Profile)
    {
    }

    /// <summary>
    /// Full-control constructor. Pass <paramref name="profile"/> as
    /// <c>null</c> from tests to skip the
    /// <see cref="ProfileService.ProfileLoaded"/>/<see cref="ProfileService.ProfileMutated"/>
    /// subscriptions — <see cref="LocalRank"/> then stays at its
    /// <see cref="PartyRank.Mid"/> default. Production code uses the
    /// two-arg overload above; that path grabs the live
    /// <see cref="AppServices.Profile"/>.
    /// </summary>
    public PartyViewModel(PartyState state, Action<byte[]>? wireSender, ProfileService? profile)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _wireSender = wireSender;
        _profile = profile;

        State.Members.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HeaderText));
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

    /// <summary>
    /// Per-row Uninvite. Only sends a wire command when the local
    /// character is the party leader — otherwise the in-game command
    /// would just bounce back as "you're not the leader" noise.
    /// </summary>
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
