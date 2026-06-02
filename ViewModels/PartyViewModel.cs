using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
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
/// PR 6.6 ships the compact view: leader star, name, HP/MA values + bars,
/// status-flag indicator stubs (the flags themselves are PR 6.5b — see
/// the Phase 6 doc), and a per-row Uninvite button. Detail-mode toggle
/// (adds Class / Race / Position columns + Rank chip) is a follow-up.
/// </para>
/// <para>
/// Uninvite: emits <c>uninvite &lt;name&gt;</c> on the wire when the
/// local character is the party leader (the button is disabled otherwise
/// because non-leaders can't actually remove members in MajorMUD).
/// Routes through the same <c>SendUserInput</c> the macro / trigger /
/// remote-command paths use.
/// </para>
/// </remarks>
public sealed partial class PartyViewModel : ObservableObject
{
    private readonly Action<byte[]>? _wireSender;

    public PartyState State { get; }

    /// <summary>"Party (N)" header text — recomputes when membership changes.</summary>
    public string HeaderText => $"Party ({State.Members.Count})";

    public PartyViewModel(PartyState state, Action<byte[]>? wireSender = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _wireSender = wireSender;
        // Bubble Members.CollectionChanged into HeaderText so the title
        // line updates as members come and go.
        State.Members.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HeaderText));
        State.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HeaderText));
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
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes($"uninvite {member.Name}\r");
        _wireSender(bytes);
    }
}
