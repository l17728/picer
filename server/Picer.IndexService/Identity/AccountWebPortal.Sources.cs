using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace Picer.IndexService.Identity;

public static partial class AccountWebPortal
{
    private static void MapSources(WebApplication app, AccountStore store)
    {
        app.MapGet("/account/sources", async (HttpContext context, IAntiforgery antiforgery, SpaceRuntimePool pool) =>
        {
            var account = Subject(context);
            if (account is null) return Results.Redirect("/account/login");
            var spaceId = context.Request.Query["space"].ToString();
            var space = store.ListSpaces(account).SingleOrDefault(item => item.Id == spaceId && item.Role == "owner")
                ?? throw new UnauthorizedAccessException();
            await using var lease = await pool.AcquireAsync(spaceId, context.RequestAborted);
            using var database = lease.Runtime.OpenDatabase();
            var agents = store.ListDevices(account).Where(item => item.SpaceId == spaceId && item.Kind == "agent" && item.State == "active").ToArray();
            var body = new StringBuilder("<p>请先在照片所在电脑授权驻留程序并选择目录，再将下列索引来源关联到该设备。此操作不移动照片、不删除索引，也不会自动开放驻留程序未授权的目录。</p>");
            using var query = database.Command("SELECT source_id,original_path,agent_grant_id FROM sync_source_bindings ORDER BY source_id");
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                var sourceId = rows.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var bound = rows.IsDBNull(2) ? null : rows.GetString(2);
                var fields = new StringBuilder("<input type=hidden name=space value='").Append(E(spaceId))
                    .Append("'><input type=hidden name=source value='").Append(sourceId).Append("'><label>来源目录：")
                    .Append(E(rows.GetString(1))).Append("（索引来源 ").Append(sourceId).Append("）</label><label>提供媒体的设备<select name=agent><option value=''>不绑定设备</option>");
                foreach (var agent in agents)
                    fields.Append("<option value='").Append(E(agent.Id)).Append("'").Append(agent.Id == bound ? " selected" : "")
                        .Append(">").Append(E(agent.Name)).Append(" · ").Append(E(agent.Id[..8])).Append("</option>");
                fields.Append("</select></label><button>保存来源关联</button>");
                body.Append("<article>").Append(Form(context, antiforgery, "/account/sources", "", fields.ToString())).Append("</article>");
            }
            if (agents.Length == 0) body.Append("<p>当前没有可绑定的驻留程序。请在来源电脑的驻留程序设置中登录此账号并选择相同空间。</p>");
            body.Append("<p><a href='/account'>返回照片空间</a></p>");
            return Page(space.Name + "：来源设备", body.ToString());
        });
        app.MapPost("/account/sources", async (HttpContext context, IAntiforgery antiforgery, SpaceRuntimePool pool) =>
        {
            var account = Subject(context) ?? throw new UnauthorizedAccessException();
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var spaceId = form["space"].ToString();
            if (!long.TryParse(form["source"], out var sourceId) || sourceId <= 0) throw new ArgumentException("invalid_source");
            // Validate before acquiring a runtime, then revalidate under the authorization fence at commit.
            if (!store.ListSpaces(account).Any(item => item.Id == spaceId && item.Role == "owner")) throw new UnauthorizedAccessException();
            await using var lease = await pool.AcquireAsync(spaceId, context.RequestAborted);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await lease.Runtime.MutationGate.WaitAsync(deadline.Token);
            try
            {
                using var database = lease.Runtime.OpenDatabase();
                store.BindSourceAsOwner(account, spaceId, form["agent"].ToString(), database, sourceId);
            }
            finally { lease.Runtime.MutationGate.Release(); }
            return Results.Redirect("/account/sources?space=" + spaceId);
        });
    }
}
