using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;
using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.IndexService.Identity;

public sealed record AuthorizedDevice(string GrantId, string AccountId, string SpaceId, string DeviceId,
    string PublicKey, string Kind, string Scope, long AuthEpoch, long DataEpoch)
{
    public bool Allows(string scope) => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);
    public long SecurityEpoch { get; init; }
    public long MembershipVersion { get; init; }
}
public sealed record RegisteredDevice(string Id, string SpaceId, string Name, string Kind, string Scope, string State);

public sealed partial class AccountStore
{
    public const string ViewerScopes = "index.read thumb.read";
    public const string EditorScopes = "index.read thumb.read index.write";
    public const string OwnerScopes = EditorScopes + " index.purge source.publish";
    public const string AgentScopes = "source.serve";

    public string CreateEnrollment(DeviceEnrollmentRequest request)
    {
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || request.DeviceName is null || request.DeviceName.Length is < 1 or > 80 ||
            request.DeviceName.Any(char.IsControl) || request.ClientKind is not ("gui" or "agent"))
            throw new ArgumentException("invalid_enrollment_request");
        using var key = DeviceAuthorization.ReadPublicKey(request.PublicKey);
        var canonical = DeviceAuthorization.ExportPublicKey(key);
        if (request.PublicKey != canonical) throw new ArgumentException("invalid_device_key");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var existing = Command(connection, transaction, "SELECT public_key,name,kind,state,expires FROM enrollments WHERE id=$id", ("$id", request.RequestId)))
        using (var row = existing.ExecuteReader())
        {
            if (row.Read())
            {
                if (row.GetString(0) != canonical || row.GetString(1) != request.DeviceName || row.GetString(2) != request.ClientKind ||
                    row.GetString(3) is not ("requested" or "approved") || row.GetInt64(4) <= Now)
                    throw new UnauthorizedAccessException("enrollment_unavailable");
                row.Close(); transaction.Commit(); return request.RequestId;
            }
        }
        Execute(connection, transaction, "DELETE FROM enrollments WHERE state='requested' AND expires<$now", ("$now", Now));
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM enrollments WHERE state='requested'")) >= 1000)
            throw new InvalidOperationException("enrollment_capacity_exceeded");
        Execute(connection, transaction, "INSERT INTO enrollments(id,public_key,name,kind,created,expires) VALUES($id,$key,$name,$kind,$now,$expires)",
            ("$id", request.RequestId), ("$key", canonical), ("$name", request.DeviceName), ("$kind", request.ClientKind), ("$now", Now), ("$expires", Now + 900));
        transaction.Commit(); return request.RequestId;
    }

    public DeviceEnrollmentRequest? GetEnrollment(string requestId)
    {
        using var connection = Open(); using var command = Command(connection, null,
            "SELECT id,public_key,name,kind FROM enrollments WHERE id=$id AND state='requested' AND expires>$now", ("$id", requestId), ("$now", Now));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
    }

    public DeviceAuthorizationFile ApproveEnrollment(string accountId, string requestId, string spaceId, string origin,
        string spkiPin, ECDsa signingKey, bool allowEditing, bool allowMedia, bool allowAnalysis = false)
    {
        origin = DeviceAuthorization.NormalizeOrigin(origin);
        var code = DeviceAuthorization.NewSecret();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        string role, accountName, spaceName; long version;
        using (var command = Command(connection, transaction,
            "SELECT m.role,m.version,a.login,s.name FROM memberships m JOIN accounts a ON a.id=m.account_id JOIN spaces s ON s.id=m.space_id WHERE m.account_id=$account AND m.space_id=$space AND a.disabled=0 AND a.must_change_password=0 AND s.state='active'",
            ("$account", accountId), ("$space", spaceId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new UnauthorizedAccessException("space_unavailable");
            role = reader.GetString(0); version = reader.GetInt64(1); accountName = reader.GetString(2); spaceName = reader.GetString(3);
        }
        string publicKey, kind;
        using (var command = Command(connection, transaction,
            "SELECT public_key,kind FROM enrollments WHERE id=$id AND state='requested' AND expires>$now", ("$id", requestId), ("$now", Now)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("enrollment_unavailable");
            publicKey = reader.GetString(0); kind = reader.GetString(1);
        }
        // Approval is for the authenticated person's own device, never implicit impersonation of another member.
        if (kind == "agent" && role != "owner") throw new UnauthorizedAccessException("source_approval_requires_owner");
        var scope = kind == "agent" ? AgentScopes : allowEditing && role != "viewer" ? (role == "owner" ? OwnerScopes : EditorScopes) : ViewerScopes;
        if (kind == "gui" && allowMedia && !scope.Split(' ').Contains("media.read")) scope += " media.read";
        if (kind == "gui" && allowAnalysis && role != "viewer") scope += " analysis.run answer.run";
        Execute(connection, transaction, """
            UPDATE enrollments SET state='approved',subject_id=$account,approver_id=$account,space_id=$space,
            membership_version=$version,scope=$scope,code_hash=$code,approved=$now,expires=$expires WHERE id=$id AND state='requested'
            """, ("$account", accountId), ("$space", spaceId), ("$version", version), ("$scope", scope),
            ("$code", DeviceAuthorization.Digest(code)), ("$now", Now), ("$expires", Now + 900), ("$id", requestId));
        var document = DeviceAuthorization.Sign(new(1, ServiceId, origin, spkiPin, requestId, spaceId, accountId,
            scope, DateTimeOffset.FromUnixTimeSeconds(Now + 900), code, publicKey, "") { MembershipVersion = version, AccountName = accountName, SpaceName = spaceName }, signingKey);
        transaction.Commit(); return document;
    }

    public DeviceSession Redeem(string requestId, string code, string publicKey, string attemptId)
    {
        if (!Guid.TryParseExact(attemptId, "N", out _) || code is null || code.Length > 128) throw new ArgumentException("invalid_redemption");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        string account, space, key, kind, name, scope, state; long membership, expiry;
        using (var command = Command(connection, transaction, "SELECT subject_id,space_id,public_key,kind,name,scope,state,membership_version,expires FROM enrollments WHERE id=$id AND code_hash=$code",
            ("$id", requestId), ("$code", DeviceAuthorization.Digest(code))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new UnauthorizedAccessException("enrollment_unavailable");
            account = reader.GetString(0); space = reader.GetString(1); key = reader.GetString(2); kind = reader.GetString(3);
            name = reader.GetString(4); scope = reader.GetString(5); state = reader.GetString(6); membership = reader.GetInt64(7); expiry = reader.GetInt64(8);
        }
        if (key != publicKey || expiry <= Now) throw new UnauthorizedAccessException("enrollment_unavailable");
        var authority = ReadAuthority(connection, transaction, account, space, membership);
        if (state == "redeemed")
        {
            var replay = ReplayResponse(connection, transaction, "enrollment:" + requestId, attemptId);
            transaction.Commit(); return replay ?? throw new UnauthorizedAccessException("enrollment_already_used");
        }
        if (state != "approved") throw new UnauthorizedAccessException("enrollment_unavailable");
        var grant = Guid.NewGuid().ToString("N"); var device = Guid.NewGuid().ToString("N");
        Execute(connection, transaction, "INSERT INTO device_grants(id,account_id,space_id,device_id,public_key,name,kind,scope,created) VALUES($id,$account,$space,$device,$key,$name,$kind,$scope,$now)",
            ("$id", grant), ("$account", account), ("$space", space), ("$device", device), ("$key", key), ("$name", name), ("$kind", kind), ("$scope", scope), ("$now", Now));
        var session = CreateSession(connection, transaction, grant, account, space, device, scope, kind,
            authority.SecurityEpoch, authority.AuthEpoch, membership, Guid.NewGuid().ToString("N"), null);
        Execute(connection, transaction, "UPDATE enrollments SET state='redeemed',attempt_id=$attempt,grant_id=$grant WHERE id=$id",
            ("$attempt", attemptId), ("$grant", grant), ("$id", requestId));
        SaveResponse(connection, transaction, "enrollment:" + requestId, attemptId, session);
        transaction.Commit(); return session;
    }

    private (long SecurityEpoch, long AuthEpoch) ReadAuthority(SqliteConnection connection, SqliteTransaction? transaction,
        string account, string space, long membershipVersion)
    {
        using var command = Command(connection, transaction, """
            SELECT a.security_epoch,s.auth_epoch FROM accounts a JOIN memberships m ON m.account_id=a.id JOIN spaces s ON s.id=m.space_id
            WHERE a.id=$account AND s.id=$space AND a.disabled=0 AND s.state='active' AND m.version=$membership
            """, ("$account", account), ("$space", space), ("$membership", membershipVersion));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new UnauthorizedAccessException("authorization_revoked");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private DeviceSession CreateSession(SqliteConnection connection, SqliteTransaction transaction, string grant, string account,
        string space, string device, string scope, string kind, long securityEpoch, long authEpoch, long membership,
        string family, long? absoluteExpiry)
    {
        var access = DeviceAuthorization.NewSecret(); var refresh = DeviceAuthorization.NewSecret();
        var absolute = absoluteExpiry ?? Now + (kind == "agent" ? 365L : 90L) * 86400;
        Execute(connection, transaction, """
            INSERT INTO sessions(access_hash,refresh_hash,grant_id,access_expires,refresh_expires,absolute_expires,security_epoch,auth_epoch,membership_version,family_id)
            VALUES($access,$refresh,$grant,$accessExpires,$refreshExpires,$absolute,$security,$auth,$membership,$family)
            """, ("$access", DeviceAuthorization.Digest(access)), ("$refresh", DeviceAuthorization.Digest(refresh)), ("$grant", grant),
            ("$accessExpires", Now + 300), ("$refreshExpires", Math.Min(absolute, Now + (kind == "agent" ? 90L : 30L) * 86400)),
            ("$absolute", absolute), ("$security", securityEpoch), ("$auth", authEpoch), ("$membership", membership), ("$family", family));
        return new(ServiceId, space, device, grant, account, scope, access, refresh, DateTimeOffset.FromUnixTimeSeconds(Now + 300))
        {
            AccountName = Scalar(connection, transaction, "SELECT login FROM accounts WHERE id=$id", ("$id", account)) as string ?? "",
            SpaceName = Scalar(connection, transaction, "SELECT name FROM spaces WHERE id=$id", ("$id", space)) as string ?? ""
        };
    }

    public AuthorizedDevice? Authorize(string accessToken, string publicKey, string? expectedSpace = null)
    {
        if (accessToken.Length > 128) return null;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AuthorizedDevice? result;
        using (var command = Command(connection, transaction, """
            SELECT g.id,g.account_id,g.space_id,g.device_id,g.public_key,g.kind,g.scope,s.auth_epoch,s.data_epoch,a.security_epoch,m.version
            FROM sessions t JOIN device_grants g ON g.id=t.grant_id JOIN accounts a ON a.id=g.account_id
            JOIN spaces s ON s.id=g.space_id JOIN memberships m ON m.space_id=s.id AND m.account_id=a.id
            WHERE t.access_hash=$hash AND t.revoked=0 AND t.access_expires>$now AND g.state IN ('pending','active')
            AND (g.state='active' OR g.created+300>$now) AND a.disabled=0 AND s.state='active' AND g.public_key=$key
            AND t.security_epoch=a.security_epoch AND t.auth_epoch=s.auth_epoch AND t.membership_version=m.version
            """, ("$hash", DeviceAuthorization.Digest(accessToken)), ("$now", Now), ("$key", publicKey)))
        using (var reader = command.ExecuteReader())
            result = reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8))
                { SecurityEpoch = reader.GetInt64(9), MembershipVersion = reader.GetInt64(10) } : null;
        if (result is null || expectedSpace is not null && result.SpaceId != expectedSpace) return null;
        Execute(connection, transaction, "UPDATE device_grants SET state='active',activated=$now WHERE id=$id AND state='pending'", ("$now", Now), ("$id", result.GrantId));
        transaction.Commit(); return result;
    }

    public bool IsAuthorizationCurrent(AuthorizedDevice device)
    {
        using var connection = Open();
        return Convert.ToInt64(Scalar(connection, null, """
            SELECT COUNT(*) FROM device_grants g JOIN accounts a ON a.id=g.account_id JOIN spaces s ON s.id=g.space_id
            JOIN memberships m ON m.account_id=a.id AND m.space_id=s.id
            WHERE g.id=$grant AND g.state='active' AND a.disabled=0 AND s.state='active' AND g.scope=$scope
            AND a.security_epoch=$security AND s.auth_epoch=$auth AND s.data_epoch=$data AND m.version=$membership
            AND EXISTS(SELECT 1 FROM sessions t WHERE t.grant_id=g.id AND t.revoked=0 AND t.refresh_expires>$now AND t.absolute_expires>$now)
            """, ("$grant", device.GrantId), ("$scope", device.Scope), ("$security", device.SecurityEpoch), ("$auth", device.AuthEpoch),
            ("$data", device.DataEpoch), ("$membership", device.MembershipVersion), ("$now", Now))) == 1;
    }

    private void SaveResponse(SqliteConnection connection, SqliteTransaction transaction, string key, string attemptId, DeviceSession response)
    {
        Execute(connection, transaction, "DELETE FROM credential_responses WHERE expires<$now", ("$now", Now));
        Execute(connection, transaction, "INSERT INTO credential_responses(request_key,attempt_id,grant_id,protected_response,expires) VALUES($key,$attempt,$grant,$response,$expires)",
            ("$key", key), ("$attempt", attemptId), ("$grant", response.GrantId),
            ("$response", _responses.Protect(JsonSerializer.Serialize(response, DeviceAuthorization.Json))), ("$expires", Now + 30));
    }

    private DeviceSession? ReplayResponse(SqliteConnection connection, SqliteTransaction transaction, string key, string attemptId)
    {
        using var command = Command(connection, transaction, """
            SELECT r.protected_response FROM credential_responses r JOIN device_grants g ON g.id=r.grant_id
            JOIN accounts a ON a.id=g.account_id JOIN memberships m ON m.account_id=a.id AND m.space_id=g.space_id
            JOIN spaces s ON s.id=g.space_id
            WHERE r.request_key=$key AND r.attempt_id=$attempt AND r.expires>$now AND g.state IN ('pending','active')
            AND a.disabled=0 AND s.state='active'
            """, ("$key", key), ("$attempt", attemptId), ("$now", Now));
        var value = command.ExecuteScalar() as string;
        if (value is null) return null;
        var response = JsonSerializer.Deserialize<DeviceSession>(_responses.Unprotect(value), DeviceAuthorization.Json)!;
        // Do not replay a cached response after a security/membership epoch transition.
        var valid = Convert.ToInt64(Scalar(connection, transaction, """
            SELECT COUNT(*) FROM sessions t JOIN device_grants g ON g.id=t.grant_id JOIN accounts a ON a.id=g.account_id
            JOIN spaces s ON s.id=g.space_id JOIN memberships m ON m.space_id=s.id AND m.account_id=a.id
            WHERE t.access_hash=$hash AND t.revoked=0 AND t.security_epoch=a.security_epoch AND t.auth_epoch=s.auth_epoch AND t.membership_version=m.version
            """, ("$hash", DeviceAuthorization.Digest(response.AccessToken)))) == 1;
        return valid ? response : null;
    }

    public DeviceSession Refresh(string refreshToken, string publicKey, string attemptId)
    {
        if (refreshToken is null || refreshToken.Length > 128 || !Guid.TryParseExact(attemptId, "N", out _)) throw new UnauthorizedAccessException("invalid_refresh");
        using var fence = EnterRevocationFence();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var hash = DeviceAuthorization.Digest(refreshToken);
        string grant, account, space, device, kind, scope, family; long security, auth, membership, absolute, revoked;
        using (var command = Command(connection, transaction, """
            SELECT g.id,g.account_id,g.space_id,g.device_id,g.kind,g.scope,t.family_id,t.security_epoch,t.auth_epoch,t.membership_version,t.absolute_expires,t.revoked
            FROM sessions t JOIN device_grants g ON g.id=t.grant_id
            WHERE t.refresh_hash=$hash AND g.public_key=$key AND g.state='active' AND t.refresh_expires>$now AND t.absolute_expires>$now
            """, ("$hash", hash), ("$key", publicKey), ("$now", Now)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new UnauthorizedAccessException("invalid_refresh");
            grant = reader.GetString(0); account = reader.GetString(1); space = reader.GetString(2); device = reader.GetString(3);
            kind = reader.GetString(4); scope = reader.GetString(5); family = reader.GetString(6); security = reader.GetInt64(7);
            auth = reader.GetInt64(8); membership = reader.GetInt64(9); absolute = reader.GetInt64(10); revoked = reader.GetInt64(11);
        }
        var authority = ReadAuthority(connection, transaction, account, space, membership);
        if (authority.SecurityEpoch != security || authority.AuthEpoch != auth) throw new UnauthorizedAccessException("authorization_revoked");
        if (revoked != 0)
        {
            var replay = ReplayResponse(connection, transaction, "refresh:" + hash, attemptId);
            if (replay is not null) { transaction.Commit(); return replay; }
            Execute(connection, transaction, "UPDATE sessions SET revoked=1 WHERE family_id=$family", ("$family", family));
            Execute(connection, transaction, "DELETE FROM credential_responses WHERE grant_id=$grant", ("$grant", grant));
            transaction.Commit(); throw new UnauthorizedAccessException("refresh_replay_detected");
        }
        Execute(connection, transaction, "UPDATE sessions SET revoked=1 WHERE refresh_hash=$hash", ("$hash", hash));
        var session = CreateSession(connection, transaction, grant, account, space, device, scope, kind, security, auth, membership, family, absolute);
        SaveResponse(connection, transaction, "refresh:" + hash, attemptId, session);
        transaction.Commit(); return session;
    }

    public IReadOnlyList<RegisteredDevice> ListDevices(string accountId)
    {
        using var connection = Open(); using var command = Command(connection, null,
            "SELECT id,space_id,name,kind,scope,state FROM device_grants WHERE account_id=$account ORDER BY created DESC", ("$account", accountId));
        using var reader = command.ExecuteReader(); var result = new List<RegisteredDevice>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    internal IReadOnlyDictionary<string,(string Name,bool Active)> ReadSpaceSourceGrants(string spaceId)
    {
        using var connection = Open();
        using var command = Command(connection,null,"""
            SELECT g.id,g.name,(g.state='active' AND g.kind='gui' AND a.disabled=0 AND s.state='active'
                AND m.role IN ('owner','editor') AND instr(' '||g.scope||' ',' index.read ')>0
                AND instr(' '||g.scope||' ',' index.write ')>0)
            FROM device_grants g JOIN accounts a ON a.id=g.account_id
            LEFT JOIN memberships m ON m.space_id=g.space_id AND m.account_id=g.account_id
            JOIN spaces s ON s.id=g.space_id
            WHERE g.space_id=$space
            """,("$space",spaceId));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string,(string Name,bool Active)>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0),(reader.GetString(1),!reader.IsDBNull(2) && reader.GetBoolean(2)));
        return result;
    }

    public void Revoke(string accountId, string grantId)
    {
        using var fence = EnterRevocationFence();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (Execute(connection, transaction, "UPDATE device_grants SET state='revoked',revoked=$now WHERE id=$id AND account_id=$account",
            ("$now", Now), ("$id", grantId), ("$account", accountId)) != 1) throw new UnauthorizedAccessException("device_unavailable");
        Execute(connection, transaction, "UPDATE sessions SET revoked=1 WHERE grant_id=$grant", ("$grant", grantId));
        Execute(connection, transaction, "DELETE FROM credential_responses WHERE grant_id=$grant", ("$grant", grantId));
        transaction.Commit();
    }
}
