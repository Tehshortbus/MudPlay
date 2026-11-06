using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// Read-only handler for `@spells` (QuerySpells category), answered from the Spell
// Book: `learned` lists every learned spell with its cast code, `unlearned` the spells
// the character could learn at their level and hasn't, and a spell's name or cast code
// says whether that one is learned.
public sealed class SpellsQueryHandler : IDisposable
{
    private const string Command = "@spells";
    private const string Usage = "usage: @spells learned | unlearned | <spell name or cast code>";

    private readonly RemoteCommandManager _engine;
    private readonly SpellbookState _book;
    private bool _disposed;

    public SpellsQueryHandler(RemoteCommandManager engine, SpellbookState book)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(book);
        _engine = engine;
        _book = book;
        if (!RemoteCommandCatalog.TryGetCategory(Command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{Command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(Command, category, OnSpells);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
    }

    private void OnSpells(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0)
        {
            ctx.Refuse(Usage);
            return;
        }
        if (_book.ClassNumber <= 0)
        {
            ctx.Refuse("spell book not read yet");
            return;
        }

        if (query.Equals("learned", StringComparison.OrdinalIgnoreCase))
        {
            List<string> learned = Learned().Select(Label).ToList();
            if (learned.Count == 0) ctx.Reply("no spells learned");
            else InventoryQueryHandler.ReplyChunked(ctx, "learned", learned);
            return;
        }

        if (query.Equals("unlearned", StringComparison.OrdinalIgnoreCase))
        {
            List<string> learnable = LearnableNow().Select(Label).ToList();
            if (learnable.Count == 0) ctx.Reply($"nothing left to learn at level {_book.Level}");
            else InventoryQueryHandler.ReplyChunked(ctx, "learnable now", learnable);
            return;
        }

        if (Find(query) is not { } spell)
        {
            ctx.Refuse($"no spell matches '{query}'");
            return;
        }
        ctx.Reply($"{Label(spell)}: {(_book.IsObtained(spell.Number) ? "learned" : "not learned")}");
    }

    private static string Label(KnownSpell s) => $"{s.Name} ({s.Short.Trim()})";

    // The learned spells in the Spell Book's order, then any the class data gives no
    // way to learn.
    private IEnumerable<KnownSpell> Learned() =>
        _book.Available.Where(s => _book.IsObtained(s.Number))
            .Concat(_book.LearnedOutsideClassData)
            .DistinctBy(s => s.Number);

    // A spell the alignment allows, not yet learned, whose level the character has
    // reached: its own required level, or a trainer's higher one.
    private IEnumerable<KnownSpell> LearnableNow()
    {
        IReadOnlyDictionary<int, int> taughtAt = _book.GetTeachLevels();
        return _book.Available.Where(s => !_book.IsObtained(s.Number)
            && Math.Max(s.ReqLevel, taughtAt.GetValueOrDefault(s.Number)) <= _book.Level);
    }

    // A cast code first, then the name: whole, then its start, then anywhere in it. A
    // learned spell wins among same-ranked matches, then the spells the alignment
    // allows, so a code two spells share resolves to the one the character can use.
    private KnownSpell? Find(string query)
    {
        List<KnownSpell> all = _book.Available.Concat(_book.LearnedOutsideClassData).Concat(_book.ClassSpells)
            .DistinctBy(s => s.Number).ToList();

        foreach (Func<KnownSpell, bool> match in new Func<KnownSpell, bool>[]
                 {
                     s => s.Short.Trim().Equals(query, StringComparison.OrdinalIgnoreCase),
                     s => s.Name.Equals(query, StringComparison.OrdinalIgnoreCase),
                     s => s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase),
                     s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
                 })
        {
            List<KnownSpell> hits = all.Where(match).ToList();
            if (hits.Count > 0) return hits.FirstOrDefault(s => _book.IsObtained(s.Number), hits[0]);
        }
        return null;
    }
}
