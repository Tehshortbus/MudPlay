using System;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

// @equip-<setname> — a permitted party member asks us to swap to one of our
// saved gear sets. The set keyword is the suffix after @equip- (e.g.
// @equip-fighting); the engine's prefix router folds it in as Args[0]. Resolves
// the set by keyword (then name) and drives EquipmentManager.ApplyByKeyword.
//
// ExecuteCommands-gated per the catalog — a "do something on my behalf" action,
// like @do / @train. Failure replies (unknown set, busy) obey WarnOnDenial; the
// success acknowledgement is sent unconditionally.
public sealed class EquipHandler : IDisposable
{
    // Bare key the catalog/@help/tooltips show; Prefix is the wire-match form.
    private const string CatalogKey = "@equip";
    private const string Prefix = "@equip-";

    private readonly RemoteCommandManager _engine;
    private readonly EquipmentManager _equipment;
    private bool _disposed;

    public EquipHandler(RemoteCommandManager engine, EquipmentManager equipment)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(equipment);
        _engine = engine;
        _equipment = equipment;

        if (!RemoteCommandCatalog.TryGetCategory(CatalogKey, out PlayerRemoteControls category))
            throw new InvalidOperationException($"RemoteCommandCatalog missing entry for '{CatalogKey}'.");
        _engine.RegisterPrefixHandler(Prefix, category, OnEquip);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterPrefixHandler(Prefix);
    }

    private void OnEquip(RemoteCommandContext ctx)
    {
        // Prefix routing folds the set keyword in as the leading arg:
        // @equip-fighting → Args[0] == "fighting". An empty suffix can't
        // reach here — the engine only prefix-matches a non-empty remainder.
        if (ctx.Args.Count == 0)
        {
            if (_engine.WarnOnDenial) ctx.Reply("usage: @equip-<set>");
            return;
        }

        string keyword = ctx.Args[0];
        switch (_equipment.ApplyByKeyword(keyword))
        {
            case EquipResult.Applied:
                ctx.Reply($"equipping gear set '{keyword}'");
                break;
            case EquipResult.NoChange:
                ctx.Reply($"gear set '{keyword}' already worn");
                break;
            case EquipResult.NotFound:
                if (_engine.WarnOnDenial) ctx.Reply($"no gear set '{keyword}'");
                break;
            case EquipResult.Busy:
                if (_engine.WarnOnDenial) ctx.Reply("busy equipping");
                break;
        }
    }
}
