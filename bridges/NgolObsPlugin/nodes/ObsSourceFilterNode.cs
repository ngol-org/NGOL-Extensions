using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ソースに掛かっているフィルタを並べ、足す・外す・入り切りする。
///
/// filter を渡さなければ一覧だけを返す。名前を推測して外れる往復を避けるため、
/// まず一覧を取ってから触る使い方を想定している。
/// フィルタの種類の識別子は obs.source.list の types から引く（一覧に混ざっている）。
///
/// 何をしても最後に一覧を返す。掛かり方は順序で変わるので、並びまで見せる。
/// </summary>
[NodeType("obs.source.filter", "OBS", "Source Filters",
    Version = "1.1.0",
    Description = "Lists the filters on a source and adds, removes or toggles them. With no filter named it only lists, which is the way to get real names instead of guessing one. Type identifiers come from the types listing, where filters sit alongside sources. Every call ends by listing again, because a filter's effect depends on where it sits in the order.")]
[NodePort("name", PortDirection.Input, "string", IsRequired = true, Description = "Source whose filters are wanted")]
[NodePort("filter", PortDirection.Input, "string", Description = "Filter to add, remove or change. Empty means list only")]
[NodePort("filter_id", PortDirection.Input, "string", Description = "Kind of filter to create, e.g. crop_filter or color_filter_v2. Given only when adding")]
[NodePort("remove", PortDirection.Input, "boolean", Description = "Take the named filter off instead of changing it")]
[NodePort("enabled", PortDirection.Input, "boolean", Description = "Turn the named filter on or off")]
[NodePort("settings", PortDirection.Input, "string", Description = "JSON object to merge into the filter's own settings")]
[NodePort("names", PortDirection.Output, "string", Description = "One filter name per line, in the order they apply")]
[NodePort("enabled_flags", PortDirection.Output, "string", Description = "true/false per line, lined up with names")]
[NodePort("count", PortDirection.Output, "number", Description = "How many filters are on the source")]
[NodePort("applied", PortDirection.Output, "boolean", Description = "true when a change was accepted")]
[NodePort("json", PortDirection.Output, "string", Description = "The whole answer")]
[NodePort("result", PortDirection.Output, "string", Description = "Human-readable outcome")]
public sealed class ObsSourceFilterNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        string name = (ctx.GetPortValue("name") as string ?? "").Trim();
        string filter = (ctx.GetPortValue("filter") as string ?? "").Trim();
        string filterId = (ctx.GetPortValue("filter_id") as string ?? "").Trim();
        string settings = (ctx.GetPortValue("settings") as string ?? "").Trim();
        bool remove = ctx.GetPortValue("remove") is bool r && r;

        ctx.SetPortValue("applied", false);

        if (name.Length == 0)
        {
            ctx.SetPortValue("result", "give the name of a source");
            return;
        }

        string did = "";
        if (filter.Length > 0)
        {
            ObsNative.Request change;
            if (remove)
            {
                change = new ObsNative.Request("filter.remove").With("name", name).With("filter", filter);
                did = "'" + filter + "' taken off";
            }
            else if (filterId.Length > 0)
            {
                change = new ObsNative.Request("filter.add")
                    .With("name", name).With("filter", filter).With("filter_id", filterId);
                if (settings.Length > 0) change.With("settings", settings);
                did = "'" + filter + "' added";
            }
            else
            {
                change = new ObsNative.Request("filter.set").With("name", name).With("filter", filter);
                if (ctx.GetPortValue("enabled") is bool enabled) change.With("enabled", enabled);
                if (settings.Length > 0) change.With("settings", settings);
                did = "'" + filter + "' changed";
            }

            using var applied = ObsNative.Call(change);
            if (!applied.Ok)
            {
                ctx.SetPortValue("json", applied.Raw);
                ctx.SetPortValue("result", applied.Error);
                return;
            }
            ctx.SetPortValue("applied", true);
        }

        using var list = ObsNative.Call(new ObsNative.Request("filter.list").With("name", name));
        ctx.SetPortValue("json", list.Raw);

        if (!list.Ok)
        {
            ctx.SetPortValue("result", list.Error);
            return;
        }

        int count = list.Count("filters");
        ctx.SetPortValue("names", list.Column("filters", "name"));
        ctx.SetPortValue("enabled_flags", list.Column("filters", "enabled"));
        ctx.SetPortValue("count", (double)count);
        ctx.SetPortValue("result", "'" + name + "' has " + count + " filter(s)"
            + (did.Length > 0 ? "; " + did : ""));
    }
}
