using System.Buffers.Binary;
using System.Data;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P275;

public sealed record P275SchemaObjectExpectation(
    string Name,
    string Type,
    string SqlSha256)
{
    public void Validate()
    {
        ValidateIdentifier(Name, nameof(Name));
        if (Type is not ("table" or "index" or "trigger" or "view"))
        {
            throw new ArgumentException("The schema object type is invalid.", nameof(Type));
        }

        ValidateHash(SqlSha256, nameof(SqlSha256));
    }

    public static P275SchemaObjectExpectation FromSql(
        string name,
        string type,
        string sql) => new(
        name,
        type,
        P275SchemaFingerprint.ComputeSqlHash(sql));

    internal static void ValidateIdentifier(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                    not (>= 'A' and <= 'Z') and
                    not (>= '0' and <= '9') and
                    not '_' and not '$'))
        {
            throw new ArgumentException("The identifier is not bounded and safe.", fieldName);
        }
    }

    internal static void ValidateHash(string value, string fieldName)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The hash must be 64 lowercase hexadecimal characters.", fieldName);
        }
    }
}

public sealed class P275SchemaExpectation
{
    public P275SchemaExpectation(IEnumerable<P275SchemaObjectExpectation> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var copy = objects.ToArray();
        if (copy.Length > P275MigrationContract.MaxSchemaItems)
        {
            throw new ArgumentException("The schema allow-list is too large.", nameof(objects));
        }

        foreach (var item in copy)
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate();
        }

        if (copy.GroupBy(item => (item.Type, item.Name)).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("The schema allow-list contains duplicates.", nameof(objects));
        }

        Objects = copy
            .OrderBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<P275SchemaObjectExpectation> Objects { get; }
}

public sealed record P275KeyTableExpectation(
    string TableName,
    IReadOnlyList<string> KeyColumns,
    long ExpectedRowCount,
    string ExpectedSha256)
{
    public void Validate()
    {
        P275SchemaObjectExpectation.ValidateIdentifier(TableName, nameof(TableName));
        ArgumentNullException.ThrowIfNull(KeyColumns);
        if (KeyColumns.Count is < 1 or > 32)
        {
            throw new ArgumentException("A key table must have one to 32 key columns.", nameof(KeyColumns));
        }

        foreach (var column in KeyColumns)
        {
            P275SchemaObjectExpectation.ValidateIdentifier(column, nameof(KeyColumns));
        }

        if (KeyColumns.Count != KeyColumns.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Key columns cannot repeat.", nameof(KeyColumns));
        }

        if (ExpectedRowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedRowCount));
        }

        P275SchemaObjectExpectation.ValidateHash(ExpectedSha256, nameof(ExpectedSha256));
    }
}

public sealed class P275CandidateVerificationExpectations
{
    public P275CandidateVerificationExpectations(
        IEnumerable<string> expectedMigrationHistory,
        P275SchemaExpectation expectedSchema,
        IEnumerable<P275KeyTableExpectation> keyTables,
        long? expectedByteLength = null,
        string? expectedSha256 = null,
        bool requireWal = true)
    {
        ArgumentNullException.ThrowIfNull(expectedMigrationHistory);
        ExpectedMigrationHistory = expectedMigrationHistory.ToArray();
        if (ExpectedMigrationHistory.Count > P275MigrationContract.MaxSchemaItems)
        {
            throw new ArgumentException("The migration history allow-list is too large.", nameof(expectedMigrationHistory));
        }

        foreach (var migrationId in ExpectedMigrationHistory)
        {
            if (string.IsNullOrWhiteSpace(migrationId) ||
                migrationId == "empty" ||
                migrationId.Length > P275MigrationContract.MaxMigrationIdBytes ||
                migrationId.Any(character =>
                    character is not (>= 'a' and <= 'z') and
                        not (>= 'A' and <= 'Z') and
                        not (>= '0' and <= '9') and
                        not '.' and not '-' and not '_'))
            {
                throw new ArgumentException("The migration history id is invalid.", nameof(expectedMigrationHistory));
            }
        }

        if (ExpectedMigrationHistory.Count != ExpectedMigrationHistory.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("The migration history cannot contain duplicates.", nameof(expectedMigrationHistory));
        }

        ExpectedSchema = expectedSchema ?? throw new ArgumentNullException(nameof(expectedSchema));
        KeyTables = keyTables?.ToArray() ?? throw new ArgumentNullException(nameof(keyTables));
        if (KeyTables.Count > P275MigrationContract.MaxSchemaItems)
        {
            throw new ArgumentException("The key table allow-list is too large.", nameof(keyTables));
        }

        foreach (var keyTable in KeyTables)
        {
            ArgumentNullException.ThrowIfNull(keyTable);
            keyTable.Validate();
        }

        if (KeyTables.GroupBy(table => table.TableName).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("The key table allow-list contains duplicates.", nameof(keyTables));
        }

        if (expectedByteLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteLength));
        }

        if (expectedSha256 is not null)
        {
            P275SchemaObjectExpectation.ValidateHash(expectedSha256, nameof(expectedSha256));
        }

        ExpectedByteLength = expectedByteLength;
        ExpectedSha256 = expectedSha256;
        RequireWal = requireWal;
    }

    public IReadOnlyList<string> ExpectedMigrationHistory { get; }

    public P275SchemaExpectation ExpectedSchema { get; }

    public IReadOnlyList<P275KeyTableExpectation> KeyTables { get; }

    public long? ExpectedByteLength { get; }

    public string? ExpectedSha256 { get; }

    public bool RequireWal { get; }
}

public sealed record P275CandidateVerificationRequest(
    P275ProfileLayout Layout,
    Guid RunId,
    P275CandidateVerificationExpectations Expectations)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Layout);
        if (RunId == Guid.Empty)
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
        }

        ArgumentNullException.ThrowIfNull(Expectations);
    }
}

public sealed record P275CandidateVerificationChecks(
    bool StandaloneFile,
    bool IntegrityCheck,
    bool ForeignKeyCheck,
    bool Schema,
    bool History,
    bool KeyTables,
    bool Hash);

public sealed record P275CandidateVerificationResult(
    bool Passed,
    string? FailureCode,
    string CandidateArtifact,
    long? ByteLength,
    string? Sha256,
    P275CandidateVerificationChecks Checks)
{
    public static P275CandidateVerificationResult Failed(
        string candidateArtifact,
        string failureCode,
        P275CandidateVerificationChecks? checks = null) => new(
        false,
        failureCode,
        candidateArtifact,
        ByteLength: null,
        Sha256: null,
        checks ?? new(false, false, false, false, false, false, false));
}

public static class P275SchemaFingerprint
{
    public static string ComputeSqlHash(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }
}

/// <summary>
/// Performs only read-only post-migration checks against the fixed staging
/// Candidate path. It does not migrate, repair, copy, promote or touch Active.
/// </summary>
public sealed class P275CandidateVerifier
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async ValueTask<P275CandidateVerificationResult> VerifyAsync(
        P275CandidateVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidateArtifact = P275ProfileLayout.GetCandidateArtifact(request.RunId);
        var checks = new P275CandidateVerificationChecks(
            StandaloneFile: false,
            IntegrityCheck: false,
            ForeignKeyCheck: false,
            Schema: false,
            History: false,
            KeyTables: false,
            Hash: false);
        try
        {
            request.Validate();
            request.Layout.EnsureCandidateForRead(request.RunId);
            var candidatePath = request.Layout.GetCandidatePath(request.RunId);
            // Sidecar lifecycle belongs to the migration/promote owner. A
            // read-only WAL open may create its own shared-memory sidecar;
            // V15 is enforced here by stable Candidate main-file size/hash
            // across the closed verification handle.
            var before = ReadFileFingerprint(candidatePath);
            checks = checks with { StandaloneFile = before.IsSqliteHeader };
            if (!before.IsSqliteHeader)
            {
                return P275CandidateVerificationResult.Failed(
                    candidateArtifact,
                    P275MigrationFailureCodes.VerifyFailed,
                    checks);
            }

            if (request.Expectations.ExpectedByteLength is { } expectedLength &&
                expectedLength != before.ByteLength)
            {
                return P275CandidateVerificationResult.Failed(
                    candidateArtifact,
                    P275MigrationFailureCodes.VerifyFailed,
                    checks);
            }

            if (request.Expectations.ExpectedSha256 is { } expectedHash &&
                !string.Equals(expectedHash, before.Sha256, StringComparison.Ordinal))
            {
                return P275CandidateVerificationResult.Failed(
                    candidateArtifact,
                    P275MigrationFailureCodes.VerifyFailed,
                    checks);
            }

            checks = checks with { StandaloneFile = true };

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = candidatePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true
            }.ToString();

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ConfigureReadOnlyAsync(connection, request.Expectations.RequireWal, cancellationToken)
                    .ConfigureAwait(false);

                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

                var integrity = await ReadIntegrityCheckAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
                {
                    return P275CandidateVerificationResult.Failed(
                        candidateArtifact,
                        P275MigrationFailureCodes.IntegrityFailed,
                        checks);
                }

                checks = checks with { IntegrityCheck = true };
                if (await HasForeignKeyViolationAsync(connection, transaction, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return P275CandidateVerificationResult.Failed(
                        candidateArtifact,
                        P275MigrationFailureCodes.ForeignKeyFailed,
                        checks);
                }

                checks = checks with { ForeignKeyCheck = true };
                if (!await SchemaMatchesAsync(
                            connection,
                            transaction,
                            request.Expectations.ExpectedSchema,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return P275CandidateVerificationResult.Failed(
                        candidateArtifact,
                        P275MigrationFailureCodes.VerifyFailed,
                        checks);
                }

                checks = checks with { Schema = true };
                bool historyMatches;
                try
                {
                    historyMatches = await HistoryMatchesAsync(
                            connection,
                            transaction,
                            request.Expectations.ExpectedMigrationHistory,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is SqliteException or
                    InvalidOperationException or
                    FormatException)
                {
                    return P275CandidateVerificationResult.Failed(
                        candidateArtifact,
                        P275MigrationFailureCodes.HistoryFailed,
                        checks);
                }

                if (!historyMatches)
                {
                    return P275CandidateVerificationResult.Failed(
                        candidateArtifact,
                        P275MigrationFailureCodes.HistoryFailed,
                        checks);
                }

                checks = checks with { History = true };
                foreach (var expected in request.Expectations.KeyTables)
                {
                    var actual = await CaptureKeyTableAsync(
                            connection,
                            transaction,
                            expected.TableName,
                            expected.KeyColumns,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (actual.ExpectedRowCount != expected.ExpectedRowCount ||
                        !string.Equals(actual.ExpectedSha256, expected.ExpectedSha256, StringComparison.Ordinal))
                    {
                        return P275CandidateVerificationResult.Failed(
                            candidateArtifact,
                            P275MigrationFailureCodes.VerifyFailed,
                            checks);
                    }
                }

                transaction.Commit();
                checks = checks with { KeyTables = true };
            }

            var after = ReadFileFingerprint(candidatePath);
            if (!after.IsSqliteHeader ||
                before.ByteLength != after.ByteLength ||
                !string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal))
            {
                return P275CandidateVerificationResult.Failed(
                    candidateArtifact,
                    P275MigrationFailureCodes.VerifyFailed,
                    checks);
            }

            if (request.Expectations.ExpectedByteLength is { } finalLength &&
                finalLength != after.ByteLength ||
                request.Expectations.ExpectedSha256 is { } finalHash &&
                !string.Equals(finalHash, after.Sha256, StringComparison.Ordinal))
            {
                return P275CandidateVerificationResult.Failed(
                    candidateArtifact,
                    P275MigrationFailureCodes.VerifyFailed,
                    checks);
            }

            return new(
                true,
                FailureCode: null,
                candidateArtifact,
                after.ByteLength,
                after.Sha256,
                checks with { Hash = true });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (P275PathValidationException exception)
        {
            return P275CandidateVerificationResult.Failed(
                candidateArtifact,
                exception.FailureCode == P275MigrationFailureCodes.PathInvalid
                    ? P275MigrationFailureCodes.VerifyFailed
                    : exception.FailureCode,
                checks);
        }
        catch (Exception exception) when (
            exception is SqliteException or
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            CryptographicException or
            InvalidOperationException or
            ArgumentException or
            FormatException)
        {
            return P275CandidateVerificationResult.Failed(
                candidateArtifact,
                P275MigrationFailureCodes.VerifyFailed,
                checks);
        }
    }

    public static async ValueTask<P275KeyTableExpectation> CaptureKeyTableExpectationAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("The SQLite connection must be open.");
        }

        var expectation = await CaptureKeyTableAsync(
                connection,
                transaction: null,
                tableName,
                keyColumns,
                cancellationToken)
            .ConfigureAwait(false);
        return expectation;
    }

    private static async ValueTask ConfigureReadOnlyAsync(
        SqliteConnection connection,
        bool requireWal,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA query_only = ON;", cancellationToken)
            .ConfigureAwait(false);

        var foreignKeys = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA foreign_keys;",
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
        var queryOnly = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA query_only;",
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
        var journalMode = await ExecuteScalarStringAsync(
                connection,
                "PRAGMA journal_mode;",
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (foreignKeys != 1 || queryOnly != 1 ||
            requireWal && !string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Candidate SQLite read contract is not satisfied.");
        }
    }

    private static async ValueTask<string?> ReadIntegrityCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            "PRAGMA integrity_check;",
            transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<bool> HasForeignKeyViolationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            "PRAGMA foreign_key_check;",
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> SchemaMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        P275SchemaExpectation expected,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            """
            SELECT name, type, sql
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
              AND type IN ('table', 'index', 'trigger', 'view')
            ORDER BY type, name;
            """,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actual = new List<P275SchemaObjectExpectation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2))
            {
                return false;
            }

            actual.Add(P275SchemaObjectExpectation.FromSql(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
            if (actual.Count > P275MigrationContract.MaxSchemaItems)
            {
                return false;
            }
        }

        return actual.SequenceEqual(expected.Objects);
    }

    private static async ValueTask<bool> HistoryMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> expected,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY rowid;",
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actual = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(reader.GetString(0));
            if (actual.Count > P275MigrationContract.MaxSchemaItems)
            {
                return false;
            }
        }

        return actual.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static async ValueTask<P275KeyTableExpectation> CaptureKeyTableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken)
    {
        var expectation = new P275KeyTableExpectation(
            tableName,
            keyColumns,
            ExpectedRowCount: 0,
            ExpectedSha256: new string('0', 64));
        expectation.Validate();

        var quotedTable = QuoteIdentifier(tableName);
        await using var countCommand = CreateCommand(
            connection,
            $"SELECT COUNT(*) FROM {quotedTable};",
            transaction);
        var countValue = await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var rowCount = Convert.ToInt64(countValue, System.Globalization.CultureInfo.InvariantCulture);

        var quotedColumns = string.Join(", ", keyColumns.Select(QuoteIdentifier));
        await using var hashCommand = CreateCommand(
            connection,
            $"SELECT {quotedColumns} FROM {quotedTable} ORDER BY {quotedColumns};",
            transaction);
        await using var reader = await hashCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var actualRows = 0L;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actualRows++;
            for (var index = 0; index < reader.FieldCount; index++)
            {
                AppendValue(hash, reader.GetValue(index));
            }

            hash.AppendData([0xFF]);
        }

        if (actualRows != rowCount)
        {
            throw new InvalidOperationException("The key table changed during verification.");
        }

        return expectation with
        {
            ExpectedRowCount = rowCount,
            ExpectedSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
        };
    }

    private static void AppendValue(IncrementalHash hash, object value)
    {
        byte tag;
        byte[] bytes;
        switch (value)
        {
            case DBNull:
                tag = 0;
                bytes = [];
                break;
            case byte[] blob:
                tag = 1;
                bytes = blob;
                break;
            case string text:
                tag = 2;
                bytes = StrictUtf8.GetBytes(text);
                break;
            case long integer:
                tag = 3;
                bytes = StrictUtf8.GetBytes(integer.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case double floating:
                tag = 4;
                bytes = StrictUtf8.GetBytes(floating.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case bool boolean:
                tag = 5;
                bytes = [boolean ? (byte)1 : (byte)0];
                break;
            default:
                tag = 9;
                bytes = StrictUtf8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }

        hash.AppendData([tag]);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, commandText, transaction: null);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, commandText, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, commandText, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string QuoteIdentifier(string value)
    {
        P275SchemaObjectExpectation.ValidateIdentifier(value, nameof(value));
        return $"\"{value}\"";
    }

    private static P275FileFingerprint ReadFileFingerprint(string path)
    {
        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan
            });
        Span<byte> header = stackalloc byte[16];
        var headerRead = stream.Read(header);
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        return new(
            stream.Length,
            Convert.ToHexString(hash).ToLowerInvariant(),
            headerRead == 16 && header.SequenceEqual("SQLite format 3\0"u8));
    }

    private readonly record struct P275FileFingerprint(
        long ByteLength,
        string Sha256,
        bool IsSqliteHeader);
}
