using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;
using System.Security.Cryptography;

namespace Picer.IndexService.Identity;

/// <summary>Control-plane state only. Media is never stored in this database.</summary>
public sealed partial class AccountStore
{
    private readonly string _connectionString;
    private readonly PasswordHasher<string> _passwords = new();
    private readonly TimeProvider _clock;
    private readonly IDataProtector _responses;
    private readonly ReaderWriterLockSlim _authorizationFence = new();
    public string Root { get; }
    public string ServiceId { get; }
    public Action<string>? InitializeSpaceGuard { private get; set; }

    public bool HasSpaces
    {
        get { using var connection = Open(); return Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM spaces")) != 0; }
    }

    public AccountStore(string root, TimeProvider? clock = null)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _clock = clock ?? TimeProvider.System;
        var directory = Path.Combine(Root, "control");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "control.db"),
            Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version > 1) throw new InvalidOperationException("control_database_requires_newer_program");
        if (version == 0)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS service_identity(id INTEGER PRIMARY KEY CHECK(id=1),service_id TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS accounts(
                    id TEXT PRIMARY KEY,login TEXT NOT NULL UNIQUE,password_hash TEXT NOT NULL,
                    failed INTEGER NOT NULL DEFAULT 0,locked_until INTEGER NOT NULL DEFAULT 0,
                    security_epoch INTEGER NOT NULL DEFAULT 1,disabled INTEGER NOT NULL DEFAULT 0,
                    must_change_password INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS spaces(
                    id TEXT PRIMARY KEY,name TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'active',
                    data_epoch INTEGER NOT NULL DEFAULT 1,auth_epoch INTEGER NOT NULL DEFAULT 1,
                    created INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS memberships(
                    space_id TEXT NOT NULL REFERENCES spaces(id),account_id TEXT NOT NULL REFERENCES accounts(id),
                    role TEXT NOT NULL CHECK(role IN ('owner','editor','viewer')),version INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY(space_id,account_id));
                CREATE TABLE IF NOT EXISTS device_grants(
                    id TEXT PRIMARY KEY,account_id TEXT NOT NULL REFERENCES accounts(id),space_id TEXT NOT NULL REFERENCES spaces(id),
                    device_id TEXT NOT NULL,public_key TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,
                    scope TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'pending',created INTEGER NOT NULL,
                    activated INTEGER,revoked INTEGER);
                CREATE TABLE IF NOT EXISTS enrollments(
                    id TEXT PRIMARY KEY,public_key TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,
                    created INTEGER NOT NULL,expires INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'requested',
                    subject_id TEXT REFERENCES accounts(id),approver_id TEXT REFERENCES accounts(id),space_id TEXT REFERENCES spaces(id),
                    membership_version INTEGER,scope TEXT,code_hash TEXT UNIQUE,approved INTEGER,
                    attempt_id TEXT,grant_id TEXT REFERENCES device_grants(id));
                CREATE TABLE IF NOT EXISTS sessions(
                    access_hash TEXT PRIMARY KEY,refresh_hash TEXT NOT NULL UNIQUE,grant_id TEXT NOT NULL REFERENCES device_grants(id),
                    access_expires INTEGER NOT NULL,refresh_expires INTEGER NOT NULL,absolute_expires INTEGER NOT NULL,
                    security_epoch INTEGER NOT NULL,auth_epoch INTEGER NOT NULL,membership_version INTEGER NOT NULL,
                    family_id TEXT NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS ix_sessions_grant ON sessions(grant_id);
                CREATE TABLE IF NOT EXISTS credential_responses(
                    request_key TEXT PRIMARY KEY,attempt_id TEXT NOT NULL,grant_id TEXT NOT NULL,
                    protected_response TEXT NOT NULL,expires INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS proof_replays(
                    key_id TEXT NOT NULL,jti TEXT NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(key_id,jti));
                CREATE INDEX IF NOT EXISTS ix_proof_expiry ON proof_replays(expires);
                CREATE TABLE IF NOT EXISTS source_publishers(
                    id TEXT PRIMARY KEY,space_id TEXT NOT NULL REFERENCES spaces(id),grant_id TEXT NOT NULL REFERENCES device_grants(id),
                    library_id TEXT NOT NULL,incarnation_id TEXT NOT NULL,agent_grant_id TEXT,
                    created INTEGER NOT NULL,UNIQUE(space_id,library_id,incarnation_id));
                CREATE TABLE IF NOT EXISTS legacy_migration(
                    id INTEGER PRIMARY KEY CHECK(id=1),account_id TEXT NOT NULL,space_id TEXT NOT NULL,state TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            Execute(connection, transaction, "INSERT OR IGNORE INTO service_identity(id,service_id) VALUES(1,$id)", ("$id", Guid.NewGuid().ToString("N")));
            transaction.Commit();
        }
        ServiceId = (string)(Scalar(connection, null, "SELECT service_id FROM service_identity WHERE id=1")
            ?? throw new InvalidDataException("control_identity_missing"));
        var keys = Directory.CreateDirectory(Path.Combine(Root, "secrets", "identity-keys"));
        _responses = DataProtectionProvider.Create(keys, options =>
        {
            options.SetApplicationName("Picer.Identity." + ServiceId);
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
        }).CreateProtector("credential-response-v1");
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;");
        return connection;
    }
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] values)
    { using var command = Command(connection, transaction, sql, values); return command.ExecuteNonQuery(); }
    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] values)
    { using var command = Command(connection, transaction, sql, values); return command.ExecuteScalar(); }
    internal long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public static string NormalizeLogin(string login)
    {
        var value = login.Trim().Normalize().ToUpperInvariant();
        if (value.Length is < 3 or > 80 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("账号需为3～80个字符，不能包含空格或控制字符。");
        return value;
    }

    public string Register(string login, string password, string spaceName, bool mustChangePassword = false)
        => RegisterCore(login, password, spaceName, mustChangePassword, legacy: false);

    internal string CreateLegacyOwner(string login, string password, string spaceName)
        => RegisterCore(login, password, spaceName, mustChangePassword: true, legacy: true);

    private string RegisterCore(string login, string password, string spaceName, bool mustChangePassword, bool legacy)
    {
        login = NormalizeLogin(login);
        if (password.Length is < 12 or > 128 || password.All(char.IsWhiteSpace)) throw new ArgumentException("密码长度需为12～128个字符。");
        if (string.IsNullOrWhiteSpace(spaceName) || spaceName.Length > 80 || spaceName.Any(char.IsControl)) throw new ArgumentException("请输入有效的照片空间名称。");
        var id = Guid.NewGuid().ToString("N"); var space = Guid.NewGuid().ToString("N");
        var hash = _passwords.HashPassword(id, password);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO accounts(id,login,password_hash,must_change_password,created) VALUES($id,$login,$hash,$change,$now)",
            ("$id", id), ("$login", login), ("$hash", hash), ("$change", mustChangePassword ? 1 : 0), ("$now", Now));
        InitializeSpaceGuard?.Invoke(space);
        Execute(connection, transaction, "INSERT INTO spaces(id,name,created) VALUES($id,$name,$now)", ("$id", space), ("$name", spaceName.Trim()), ("$now", Now));
        Execute(connection, transaction, "INSERT INTO memberships(space_id,account_id,role) VALUES($space,$account,'owner')", ("$space", space), ("$account", id));
        if (legacy)
        {
            Execute(connection, transaction, "UPDATE spaces SET state='migrating' WHERE id=$space", ("$space", space));
            Execute(connection, transaction, "INSERT INTO legacy_migration(id,account_id,space_id,state) VALUES(1,$account,$space,'preparing')",
                ("$account", id), ("$space", space));
        }
        transaction.Commit();
        return id;
    }

    public string? Authenticate(string login, string password)
    {
        if (password.Length > 128) return null;
        try { login = NormalizeLogin(login); } catch (ArgumentException) { return null; }
        string? id = null, hash = null; long locked = 0; bool disabled = false;
        using (var connection = Open())
        using (var command = Command(connection, null, "SELECT id,password_hash,locked_until,disabled FROM accounts WHERE login=$login", ("$login", login)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { id = reader.GetString(0); hash = reader.GetString(1); locked = reader.GetInt64(2); disabled = reader.GetInt64(3) != 0; }
        if (id is null || hash is null)
        {
            // Similar password work for an unknown account; callers also enforce bounded request admission.
            _ = _passwords.HashPassword("unknown", password);
            return null;
        }
        if (disabled || locked > Now) return null;
        var result = _passwords.VerifyHashedPassword(id, hash, password);
        using var db = Open(); using var transaction = db.BeginTransaction();
        if (result == PasswordVerificationResult.Failed)
        {
            Execute(db, transaction, "UPDATE accounts SET failed=failed+1,locked_until=CASE WHEN failed+1>=5 THEN $until ELSE locked_until END WHERE id=$id AND password_hash=$hash",
                ("$id", id), ("$hash", hash), ("$until", Now + 900));
            transaction.Commit(); return null;
        }
        var updated = Execute(db, transaction, "UPDATE accounts SET failed=0,locked_until=0,password_hash=$new WHERE id=$id AND password_hash=$old AND disabled=0 AND locked_until<=$now",
            ("$id", id), ("$new", result == PasswordVerificationResult.SuccessRehashNeeded ? _passwords.HashPassword(id, password) : hash), ("$old", hash), ("$now", Now));
        transaction.Commit(); return updated == 1 ? id : null;
    }

    internal bool ContainsLogin(string login)
    {
        try { login = NormalizeLogin(login); }
        catch (ArgumentException) { return false; }
        using var connection = Open();
        return Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM accounts WHERE login=$login AND disabled=0", ("$login", login))) == 1;
    }

    public IReadOnlyList<AccountSpace> ListSpaces(string accountId)
    {
        using var connection = Open(); using var command = Command(connection, null,
            "SELECT s.id,s.name,m.role FROM spaces s JOIN memberships m ON m.space_id=s.id JOIN accounts a ON a.id=m.account_id WHERE m.account_id=$account AND a.disabled=0 AND s.state='active' ORDER BY s.created,s.id", ("$account", accountId));
        using var reader = command.ExecuteReader(); var result = new List<AccountSpace>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public bool IsAccountActive(string id)
    {
        using var connection = Open();
        return Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM accounts WHERE id=$id AND disabled=0", ("$id", id))) == 1;
    }

    public long? SecurityEpoch(string id)
    {
        using var connection = Open();
        var value = Scalar(connection, null, "SELECT security_epoch FROM accounts WHERE id=$id AND disabled=0", ("$id", id));
        return value is null ? null : Convert.ToInt64(value);
    }

    public bool RequiresPasswordChange(string id)
    {
        using var connection = Open();
        return Convert.ToInt64(Scalar(connection, null, "SELECT must_change_password FROM accounts WHERE id=$id AND disabled=0", ("$id", id))) == 1;
    }

    public void ChangePassword(string id, string currentPassword, string newPassword)
    {
        if (currentPassword.Length > 128 || newPassword.Length is < 12 or > 128 || newPassword.All(char.IsWhiteSpace))
            throw new ArgumentException("invalid_password");
        using var connection = Open();
        var hash = Scalar(connection, null, "SELECT password_hash FROM accounts WHERE id=$id AND disabled=0 AND locked_until<=$now",
            ("$id", id), ("$now", Now)) as string;
        if (hash is null || _passwords.VerifyHashedPassword(id, hash, currentPassword) == PasswordVerificationResult.Failed)
            throw new UnauthorizedAccessException("invalid_password");
        var replacement = _passwords.HashPassword(id, newPassword);
        using var transaction = connection.BeginTransaction();
        // An authenticated password change updates the login secret while keeping
        // explicitly enrolled devices usable. Device removal and administrative
        // password reset remain the revocation mechanisms.
        if (Execute(connection, transaction, "UPDATE accounts SET password_hash=$new,must_change_password=0,failed=0,locked_until=0 WHERE id=$id AND password_hash=$old AND disabled=0",
            ("$new", replacement), ("$id", id), ("$old", hash)) != 1) throw new UnauthorizedAccessException("account_changed");
        transaction.Commit();
    }

    internal void ResetPasswordForAdministration(string login, string newPassword)
    {
        login = NormalizeLogin(login);
        if (newPassword.Length is < 12 or > 128 || newPassword.All(char.IsWhiteSpace))
            throw new ArgumentException("invalid_password");
        using var connection = Open();
        var id = Scalar(connection, null, "SELECT id FROM accounts WHERE login=$login AND disabled=0", ("$login", login)) as string
            ?? throw new InvalidOperationException("account_not_found");
        var replacement = _passwords.HashPassword(id, newPassword);
        using var fence = EnterRevocationFence();
        using var transaction = connection.BeginTransaction();
        if (Execute(connection, transaction, "UPDATE accounts SET password_hash=$new,security_epoch=security_epoch+1,must_change_password=1,failed=0,locked_until=0 WHERE id=$id AND disabled=0",
            ("$new", replacement), ("$id", id)) != 1) throw new InvalidOperationException("account_not_found");
        Execute(connection, transaction, "DELETE FROM credential_responses WHERE grant_id IN (SELECT id FROM device_grants WHERE account_id=$id)", ("$id", id));
        transaction.Commit();
    }

    public T ExecuteAuthorizedWrite<T>(AuthorizedDevice expected, string token, Func<AuthorizedDevice, T> action)
    {
        _authorizationFence.EnterReadLock();
        try
        {
            var current = Authorize(token, expected.PublicKey, expected.SpaceId) ?? throw new UnauthorizedAccessException("authorization_revoked");
            if (current.GrantId != expected.GrantId) throw new UnauthorizedAccessException("authorization_changed");
            return action(current);
        }
        finally { _authorizationFence.ExitReadLock(); }
    }

    private IDisposable EnterRevocationFence()
    {
        _authorizationFence.EnterWriteLock();
        return new RevocationLease(_authorizationFence);
    }
    private sealed class RevocationLease(ReaderWriterLockSlim fence) : IDisposable
    {
        public void Dispose() => fence.ExitWriteLock();
    }

    public string SpaceRoot(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("invalid_space");
        using var connection = Open();
        if (Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM spaces WHERE id=$id AND state='active'", ("$id", id))) != 1)
            throw new UnauthorizedAccessException("space_unavailable");
        return Path.Combine(Root, "spaces", id);
    }

    public void AcceptProof(DeviceProof.Validated proof)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM proof_replays WHERE expires<$now", ("$now", Now));
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM proof_replays")) >= 100_000)
            throw new InvalidOperationException("proof_capacity_exceeded");
        try
        {
            Execute(connection, transaction, "INSERT INTO proof_replays(key_id,jti,expires) VALUES($key,$id,$expires)",
                ("$key", proof.KeyThumbprint), ("$id", proof.Id), ("$expires", Now + 120));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new InvalidDataException("dpop_replayed", exception); }
        transaction.Commit();
    }
}
