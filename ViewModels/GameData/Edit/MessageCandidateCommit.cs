using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Shared seed/commit glue between MessageCandidateWatcher's two review surfaces
// (the LogPane double-click handler in App.axaml.cs, and the Game Data Browser's
// Unrecognized Lines tab) so neither duplicates the other's logic.
internal static class MessageCandidateCommit
{
    // Build a near-blank MessageRecord seeded with the candidate's raw text in
    // CasterMessage — Name left blank deliberately, the same forcing function
    // MessagesSectionViewModel.AddAsync's blank record already uses to make the
    // user actually name what they're adding rather than save it unnamed. The
    // user can freely move the text into TargetMessage/WitnessMessage/
    // AppliedMessage inside the edit dialog if the guessed slot is wrong.
    public static MessageRecord BuildSeedRecord(MessageCandidateRecord candidate) => new(
        Id:              string.Empty,
        Name:            string.Empty,
        Flags:           MessageFlags.None,
        RawFlagsHex:     0,
        CasterMessage:   candidate.RawText,
        TargetMessage:   string.Empty,
        WitnessMessage:  string.Empty,
        AppliedMessage:  string.Empty,
        AppliedEndsWith: string.Empty,
        Links:           Array.Empty<GameDataLink>());

    // Commit a saved edit into the real Messages catalogue — mirrors
    // MessagesSectionViewModel.ApplyResult's find-by-Id-or-append + Save exactly
    // — then removes the resolved candidate; it's real data now, no longer a
    // candidate.
    public static void Commit(MessageStore messages, MessageCandidateStore candidates,
        MessageEditResult result, string candidateId)
    {
        int idx = -1;
        for (int i = 0; i < messages.Messages.Count; i++)
        {
            if (messages.Messages[i].Id == result.Original.Id) { idx = i; break; }
        }
        if (idx >= 0) messages.Messages[idx] = result.Updated;
        else          messages.Messages.Add(result.Updated);
        messages.Save();

        candidates.Remove(candidateId);
    }
}
