using Picer.Core.Data;

namespace Picer.Core.People;

public sealed record PersonRecord(long Id, string DisplayName, string Description, IReadOnlyList<string> Aliases);
public sealed record PersonSummary(long Id, string DisplayName, int ConfirmedFaceCount, int PhotoCount);
public sealed record PersonQueryMention(PersonRecord Person, string MatchedName, string RemainingQuery);

/// <summary>
/// Stores user-confirmed identities separately from clustering suggestions.
/// User assignments and rejections always take precedence over later model runs.
/// </summary>
public sealed class PeopleService(PicerDatabase database)
{
    public PersonRecord Create(string displayName, IEnumerable<string>? aliases = null)
    {
        displayName = NormalizeName(displayName);
        using var transaction = database.BeginTransaction();
        using var insert = database.Command("""
            INSERT INTO people(display_name, created_utc, updated_utc)
            VALUES ($name, $now, $now); SELECT last_insert_rowid();
            """);
        insert.Transaction = transaction;
        insert.Parameters.AddWithValue("$name", displayName);
        insert.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        var id = (long)insert.ExecuteScalar()!;
        AddAliasCore(id, displayName, transaction);
        foreach (var alias in aliases ?? [])
            AddAliasCore(id, NormalizeName(alias), transaction);
        transaction.Commit();
        return Get(id)!;
    }

    public void Rename(long personId, string displayName)
    {
        displayName = NormalizeName(displayName);
        using var transaction = database.BeginTransaction();
        EnsurePersonExists(personId, transaction);
        using var update = database.Command("UPDATE people SET display_name=$name, updated_utc=$now WHERE id=$id");
        update.Transaction = transaction;
        update.Parameters.AddWithValue("$name", displayName);
        update.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        update.Parameters.AddWithValue("$id", personId);
        update.ExecuteNonQuery();
        AddAliasCore(personId, displayName, transaction);
        transaction.Commit();
    }

    public void AddAlias(long personId, string alias)
    {
        using var transaction = database.BeginTransaction();
        EnsurePersonExists(personId, transaction);
        AddAliasCore(personId, NormalizeName(alias), transaction);
        transaction.Commit();
    }

    public void SetDescription(long personId, string? description)
    {
        using var command = database.Command("UPDATE people SET description=$description, updated_utc=$now WHERE id=$id");
        command.Parameters.AddWithValue("$description", (description ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", personId);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("人物不存在。");
    }

    public void Merge(long sourcePersonId, long targetPersonId)
    {
        if (sourcePersonId == targetPersonId) throw new ArgumentException("不能将人物合并到自己。");
        using var transaction = database.BeginTransaction();
        EnsurePersonExists(sourcePersonId, transaction);
        EnsurePersonExists(targetPersonId, transaction);
        using (var corrections = database.Command("""
            INSERT INTO face_corrections(face_id,old_person_id,new_person_id,action,created_utc)
            SELECT face_id,$source,$target,'merge_person',$now FROM face_person_assignments WHERE person_id=$source
            """))
        {
            corrections.Transaction = transaction;
            corrections.Parameters.AddWithValue("$source", sourcePersonId);
            corrections.Parameters.AddWithValue("$target", targetPersonId);
            corrections.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            corrections.ExecuteNonQuery();
        }
        using (var assignments = database.Command("UPDATE face_person_assignments SET person_id=$target,updated_utc=$now WHERE person_id=$source"))
        {
            assignments.Transaction = transaction;
            assignments.Parameters.AddWithValue("$source", sourcePersonId);
            assignments.Parameters.AddWithValue("$target", targetPersonId);
            assignments.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            assignments.ExecuteNonQuery();
        }
        using (var duplicateAliases = database.Command("""
            DELETE FROM person_aliases AS source_alias
            WHERE source_alias.person_id=$source AND EXISTS(
                SELECT 1 FROM person_aliases target_alias
                WHERE target_alias.person_id=$target AND target_alias.alias=source_alias.alias)
            """))
        {
            duplicateAliases.Transaction = transaction;
            duplicateAliases.Parameters.AddWithValue("$source", sourcePersonId);
            duplicateAliases.Parameters.AddWithValue("$target", targetPersonId);
            duplicateAliases.ExecuteNonQuery();
        }
        using (var aliases = database.Command("UPDATE person_aliases SET person_id=$target WHERE person_id=$source"))
        {
            aliases.Transaction = transaction;
            aliases.Parameters.AddWithValue("$source", sourcePersonId);
            aliases.Parameters.AddWithValue("$target", targetPersonId);
            aliases.ExecuteNonQuery();
        }
        using (var remove = database.Command("DELETE FROM people WHERE id=$source"))
        {
            remove.Transaction = transaction;
            remove.Parameters.AddWithValue("$source", sourcePersonId);
            remove.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ConfirmFace(long faceId, long personId) => Assign(faceId, personId, "confirmed", "user", null);

    public bool SuggestFace(long faceId, long personId, double confidence)
    {
        using var rejected = database.Command("SELECT 1 FROM face_person_rejections WHERE face_id=$face AND person_id=$person");
        rejected.Parameters.AddWithValue("$face", faceId);
        rejected.Parameters.AddWithValue("$person", personId);
        if (rejected.ExecuteScalar() is not null) return false;

        using var existing = database.Command("SELECT state FROM face_person_assignments WHERE face_id=$face");
        existing.Parameters.AddWithValue("$face", faceId);
        if (string.Equals(existing.ExecuteScalar() as string, "confirmed", StringComparison.Ordinal)) return false;

        using var samePhoto = database.Command("""
            SELECT 1
            FROM detected_faces candidate
            JOIN detected_faces sibling ON sibling.image_id=candidate.image_id AND sibling.id<>candidate.id
            JOIN face_person_assignments assignment ON assignment.face_id=sibling.id
            WHERE candidate.id=$face AND assignment.person_id=$person
            LIMIT 1
            """);
        samePhoto.Parameters.AddWithValue("$face", faceId);
        samePhoto.Parameters.AddWithValue("$person", personId);
        if (samePhoto.ExecuteScalar() is not null) return false;

        Assign(faceId, personId, "suggested", "cluster", confidence);
        return true;
    }

    public void MarkNotThisPerson(long faceId, long personId)
    {
        using var transaction = database.BeginTransaction();
        EnsureFaceExists(faceId, transaction);
        EnsurePersonExists(personId, transaction);
        using (var reject = database.Command("""
            INSERT OR IGNORE INTO face_person_rejections(face_id, person_id, created_utc)
            VALUES($face, $person, $now)
            """))
        {
            reject.Transaction = transaction;
            reject.Parameters.AddWithValue("$face", faceId);
            reject.Parameters.AddWithValue("$person", personId);
            reject.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            reject.ExecuteNonQuery();
        }
        using (var remove = database.Command("DELETE FROM face_person_assignments WHERE face_id=$face AND person_id=$person"))
        {
            remove.Transaction = transaction;
            remove.Parameters.AddWithValue("$face", faceId);
            remove.Parameters.AddWithValue("$person", personId);
            remove.ExecuteNonQuery();
        }
        InsertCorrection(faceId, personId, null, "not_this_person", transaction);
        transaction.Commit();
    }

    public void MoveFace(long faceId, long newPersonId)
    {
        using var old = database.Command("SELECT person_id FROM face_person_assignments WHERE face_id=$face");
        old.Parameters.AddWithValue("$face", faceId);
        var oldValue = old.ExecuteScalar();
        var oldPersonId = oldValue is null ? (long?)null : Convert.ToInt64(oldValue);

        using var transaction = database.BeginTransaction();
        EnsureFaceExists(faceId, transaction);
        EnsurePersonExists(newPersonId, transaction);
        AssignCore(faceId, newPersonId, "confirmed", "user", null, transaction);
        if (oldPersonId is not null && oldPersonId != newPersonId)
        {
            using var reject = database.Command("""
                INSERT OR IGNORE INTO face_person_rejections(face_id, person_id, created_utc)
                VALUES($face, $person, $now)
                """);
            reject.Transaction = transaction;
            reject.Parameters.AddWithValue("$face", faceId);
            reject.Parameters.AddWithValue("$person", oldPersonId.Value);
            reject.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            reject.ExecuteNonQuery();
        }
        InsertCorrection(faceId, oldPersonId, newPersonId, "move", transaction);
        transaction.Commit();
    }

    public void MoveFaceToUnassigned(long faceId)
    {
        using var transaction = database.BeginTransaction();
        EnsureFaceExists(faceId, transaction);
        using var old = database.Command("SELECT person_id FROM face_person_assignments WHERE face_id=$face");
        old.Transaction = transaction;
        old.Parameters.AddWithValue("$face", faceId);
        var value = old.ExecuteScalar();
        var oldPersonId = value is null ? (long?)null : Convert.ToInt64(value);
        using var assignment = database.Command("DELETE FROM face_person_assignments WHERE face_id=$face");
        assignment.Transaction = transaction;
        assignment.Parameters.AddWithValue("$face", faceId);
        assignment.ExecuteNonQuery();
        using var review = database.Command("DELETE FROM face_review_states WHERE face_id=$face");
        review.Transaction = transaction;
        review.Parameters.AddWithValue("$face", faceId);
        review.ExecuteNonQuery();
        InsertCorrection(faceId, oldPersonId, null, "move_to_unassigned", transaction);
        transaction.Commit();
    }

    public void IgnoreFace(long faceId)
    {
        using var transaction = database.BeginTransaction();
        EnsureFaceExists(faceId, transaction);
        using var old = database.Command("SELECT person_id FROM face_person_assignments WHERE face_id=$face");
        old.Transaction = transaction;
        old.Parameters.AddWithValue("$face", faceId);
        var value = old.ExecuteScalar();
        var oldPersonId = value is null ? (long?)null : Convert.ToInt64(value);
        using var assignment = database.Command("DELETE FROM face_person_assignments WHERE face_id=$face");
        assignment.Transaction = transaction;
        assignment.Parameters.AddWithValue("$face", faceId);
        assignment.ExecuteNonQuery();
        using var candidate = database.Command("DELETE FROM face_candidate_members WHERE face_id=$face");
        candidate.Transaction = transaction;
        candidate.Parameters.AddWithValue("$face", faceId);
        candidate.ExecuteNonQuery();
        using var review = database.Command("""
            INSERT INTO face_review_states(face_id,state,updated_utc) VALUES($face,'ignored',$now)
            ON CONFLICT(face_id) DO UPDATE SET state='ignored',updated_utc=excluded.updated_utc
            """);
        review.Transaction = transaction;
        review.Parameters.AddWithValue("$face", faceId);
        review.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        review.ExecuteNonQuery();
        InsertCorrection(faceId, oldPersonId, null, "ignore", transaction);
        transaction.Commit();
    }

    public PersonRecord? FindByName(string name)
    {
        name = NormalizeName(name);
        using var command = database.Command("""
            SELECT p.id FROM people p
            LEFT JOIN person_aliases a ON a.person_id=p.id
            WHERE p.display_name=$name COLLATE NOCASE OR a.alias=$name COLLATE NOCASE
            LIMIT 1
            """);
        command.Parameters.AddWithValue("$name", name);
        var value = command.ExecuteScalar();
        return value is null ? null : Get(Convert.ToInt64(value));
    }

    public PersonQueryMention? FindMention(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        using var command = database.Command("""
            SELECT p.id, a.alias
            FROM person_aliases a JOIN people p ON p.id=a.person_id
            ORDER BY length(a.alias) DESC
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var personId = reader.GetInt64(0);
            var alias = reader.GetString(1);
            var position = query.IndexOf(alias, StringComparison.OrdinalIgnoreCase);
            if (position < 0) continue;
            var remaining = query.Remove(position, alias.Length)
                .Replace("找一下", "", StringComparison.OrdinalIgnoreCase)
                .Replace("查找", "", StringComparison.OrdinalIgnoreCase)
                .Replace("照片", "", StringComparison.OrdinalIgnoreCase)
                .Trim(' ', '，', ',', '。', '的', '在');
            return new PersonQueryMention(Get(personId)!, alias, remaining);
        }
        return null;
    }

    public IReadOnlyList<long> GetImageIds(long personId, bool confirmedOnly = true)
    {
        using var command = database.Command("""
            SELECT DISTINCT f.image_id
            FROM detected_faces f
            JOIN face_person_assignments a ON a.face_id=f.id
            WHERE a.person_id=$person AND ($confirmed=0 OR a.state='confirmed')
            ORDER BY f.image_id
            """);
        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$confirmed", confirmedOnly ? 1 : 0);
        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public PersonRecord? Get(long personId)
    {
        using var person = database.Command("SELECT display_name,description FROM people WHERE id=$id");
        person.Parameters.AddWithValue("$id", personId);
        using var personReader = person.ExecuteReader();
        if (!personReader.Read()) return null;
        var name = personReader.GetString(0);
        var description = personReader.GetString(1);
        personReader.Close();
        using var aliases = database.Command("SELECT alias FROM person_aliases WHERE person_id=$id ORDER BY alias COLLATE NOCASE");
        aliases.Parameters.AddWithValue("$id", personId);
        var values = new List<string>();
        using var reader = aliases.ExecuteReader();
        while (reader.Read()) values.Add(reader.GetString(0));
        return new PersonRecord(personId, name, description, values);
    }

    public IReadOnlyList<PersonSummary> GetAll()
    {
        using var command = database.Command("""
            SELECT p.id, p.display_name,
                   COUNT(CASE WHEN a.state='confirmed' THEN 1 END),
                   COUNT(DISTINCT CASE WHEN a.state='confirmed' THEN f.image_id END)
            FROM people p
            LEFT JOIN face_person_assignments a ON a.person_id=p.id
            LEFT JOIN detected_faces f ON f.id=a.face_id
            GROUP BY p.id, p.display_name
            ORDER BY p.display_name COLLATE NOCASE
            """);
        var people = new List<PersonSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) people.Add(new PersonSummary(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        return people;
    }

    private void Assign(long faceId, long personId, string state, string assignedBy, double? confidence)
    {
        using var transaction = database.BeginTransaction();
        EnsureFaceExists(faceId, transaction);
        EnsurePersonExists(personId, transaction);
        AssignCore(faceId, personId, state, assignedBy, confidence, transaction);
        transaction.Commit();
    }

    private void AssignCore(long faceId, long personId, string state, string assignedBy,
        double? confidence, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.Command("""
            INSERT INTO face_person_assignments(face_id, person_id, state, confidence, assigned_by, updated_utc)
            VALUES($face, $person, $state, $confidence, $by, $now)
            ON CONFLICT(face_id) DO UPDATE SET person_id=excluded.person_id, state=excluded.state,
                confidence=excluded.confidence, assigned_by=excluded.assigned_by, updated_utc=excluded.updated_utc
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$face", faceId);
        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$confidence", (object?)confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", assignedBy);
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        command.ExecuteNonQuery();
        using var review = database.Command("DELETE FROM face_review_states WHERE face_id=$face");
        review.Transaction = transaction;
        review.Parameters.AddWithValue("$face", faceId);
        review.ExecuteNonQuery();
    }

    private void AddAliasCore(long personId, string alias, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.Command("INSERT OR IGNORE INTO person_aliases(person_id, alias, created_utc) VALUES($id,$alias,$now)");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", personId);
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    private void InsertCorrection(long faceId, long? oldPersonId, long? newPersonId, string action,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.Command("""
            INSERT INTO face_corrections(face_id, old_person_id, new_person_id, action, created_utc)
            VALUES($face,$old,$new,$action,$now)
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$face", faceId);
        command.Parameters.AddWithValue("$old", (object?)oldPersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$new", (object?)newPersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    private void EnsurePersonExists(long personId, Microsoft.Data.Sqlite.SqliteTransaction transaction) =>
        EnsureExists("people", personId, "Person", transaction);

    private void EnsureFaceExists(long faceId, Microsoft.Data.Sqlite.SqliteTransaction transaction) =>
        EnsureExists("detected_faces", faceId, "Face", transaction);

    private void EnsureExists(string table, long id, string label, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.Command($"SELECT 1 FROM {table} WHERE id=$id");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException($"{label} {id} was not found.");
    }

    private static string NormalizeName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 100) throw new ArgumentException("Name must contain 1 to 100 characters.", nameof(value));
        return normalized;
    }

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("o");
}
