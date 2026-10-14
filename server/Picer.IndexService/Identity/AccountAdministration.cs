using System.Text;

namespace Picer.IndexService.Identity;

/// <summary>Offline administration. Passwords never enter command-line arguments or logs.</summary>
public static class AccountAdministration
{
    public static int RunMigration(string[] arguments, string dataRoot)
    {
        try
        {
            if (arguments.Length > 0 && arguments[0] == "--reset-account-password")
                return RunPasswordReset(arguments, dataRoot);
            var options = Parse(arguments);
            if (!options.TryGetValue("--database", out var database) || !options.TryGetValue("--owner", out var owner))
                throw new ArgumentException("missing_options");
            var accounts = new AccountStore(Path.Combine(dataRoot, "accounts-v2"));
            var guardRoot = Environment.GetEnvironmentVariable("PICER_RESTORE_GUARD_ROOT")
                ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataRoot))!, "picer-restore-guard", accounts.ServiceId);
            var guard = new RestoreGuard(guardRoot, accounts.ServiceId, allowCreate: !accounts.HasSpaces);
            var password = accounts.GetLegacyMigration() is null ? ReadPassword(options.ContainsKey("--password-stdin")) : "";
            var report = new LegacySpaceMigrator(accounts, guard).Migrate(database, owner, password,
                options.GetValueOrDefault("--space", "原有照片空间"));
            Console.WriteLine("旧索引迁移完成。首次网页登录必须修改管理员设置的初始密码。");
            Console.WriteLine("空间 ID：" + report.SpaceId);
            Console.WriteLine("原始数据库备份：" + report.BackupDirectory);
            Console.WriteLine("来源设备尚未绑定；请登录后明确授权原照片所在设备和目录。");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The underlying exception can include local data and must not escape to a service log.
            Console.Error.WriteLine("迁移未完成，未验证的空间不会对外开放。请确认服务和数据库写入程序已停止、目录可写、选项正确，再用相同参数重试。");
            return 2;
        }
    }

    private static int RunPasswordReset(string[] arguments, string dataRoot)
    {
        var options = Parse(arguments);
        if (!options.TryGetValue("--account", out var login) || !options.ContainsKey("--password-stdin"))
            throw new ArgumentException("missing_options");
        var accounts = new AccountStore(Path.Combine(dataRoot, "accounts-v2"));
        accounts.ResetPasswordForAdministration(login, ReadPassword(fromStandardInput: true));
        Console.WriteLine("璐﹀彿瀵嗙爜宸查噸缃€傚叾浠栬澶囩殑浼氳瘽宸插け鏁堬紝涓嬫鐧诲綍蹇呴』璁剧疆鏂板瘑鐮併€?");
        return 0;
    }

    private static Dictionary<string, string> Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < arguments.Length; i++)
        {
            var name = arguments[i];
            if (name == "--reset-account-password") continue;
            if (name == "--password-stdin") { if (!values.TryAdd(name, "true")) throw new ArgumentException("duplicate_option"); }
            else if (name is "--database" or "--owner" or "--space" or "--account")
            {
                if (++i >= arguments.Length || !values.TryAdd(name, arguments[i])) throw new ArgumentException("invalid_option");
            }
            else throw new ArgumentException("unknown_option");
        }
        return values;
    }

    private static string ReadPassword(bool fromStandardInput)
    {
        if (fromStandardInput)
        {
            if (!Console.IsInputRedirected) throw new ArgumentException("password_stdin_requires_pipe");
            var input = new StringBuilder();
            int character;
            while ((character = Console.In.Read()) >= 0 && character != '\n')
            {
                if (character == '\r') continue;
                if (input.Length >= 128) throw new ArgumentException("password_too_long");
                input.Append((char)character);
            }
            return input.ToString();
        }
        if (Console.IsInputRedirected) throw new ArgumentException("explicit_password_stdin_required");
        Console.Error.Write("初始密码（12～128字符，输入不回显）：");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && password.Length < 128) password.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return password.ToString();
    }
}
