using System.Globalization;
using FiscOCRApp.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FiscOCRApp.Services;

public interface IDocumentRepository
{
    void Initialize();
    Task<FiscalDocumentDto> SaveAsync(FiscalDocumentDto document, CancellationToken cancellationToken);
    Task<FiscalDocumentDto> UpdateAsync(FiscalDocumentDto document, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
    Task<FiscalDocumentDto?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<FiscalDocumentDto>> ListAsync(string? query, string? documentType, CancellationToken cancellationToken);
}

public sealed class DocumentRepository : IDocumentRepository
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _write = new(1, 1);

    public DocumentRepository(IWebHostEnvironment environment, IOptions<StorageOptions> options)
    {
        var configured = options.Value.DatabasePath;
        if (string.IsNullOrWhiteSpace(configured))
            configured = "App_Data/fiscocr.db";

        var fullPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
        fullPath = Path.GetFullPath(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _databasePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public void Initialize()
    {
        using var connection = Open();
        string[] statements =
        [
            "PRAGMA journal_mode = WAL;",
            """
            CREATE TABLE IF NOT EXISTS Documents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DocumentType TEXT NOT NULL,
                DocumentNo TEXT,
                Company TEXT NOT NULL,
                TaxNumber TEXT,
                DocumentDate TEXT,
                SubTotal TEXT NOT NULL DEFAULT '0',
                TaxTotal TEXT NOT NULL DEFAULT '0',
                Total TEXT NOT NULL,
                Currency TEXT NOT NULL,
                RawText TEXT,
                SourceFileName TEXT,
                CreatedAt TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS DocumentLines (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DocumentId INTEGER NOT NULL,
                LineNo INTEGER NOT NULL,
                Description TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                Unit TEXT NOT NULL,
                UnitPrice TEXT NOT NULL,
                Amount TEXT NOT NULL,
                FOREIGN KEY(DocumentId) REFERENCES Documents(Id) ON DELETE CASCADE
            );
            """,
            "CREATE INDEX IF NOT EXISTS IX_Documents_CreatedAt ON Documents(CreatedAt DESC);",
            "CREATE INDEX IF NOT EXISTS IX_DocumentLines_DocumentId ON DocumentLines(DocumentId);",
            "CREATE INDEX IF NOT EXISTS IX_Documents_Company ON Documents(Company);"
        ];
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        EnsureColumn(connection, "Documents", "SubTotal", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, "Documents", "TaxTotal", "TEXT NOT NULL DEFAULT '0'");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    public async Task<FiscalDocumentDto> SaveAsync(FiscalDocumentDto document, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO Documents
                    (DocumentType, DocumentNo, Company, TaxNumber, DocumentDate, SubTotal, TaxTotal, Total, Currency, RawText, SourceFileName, CreatedAt)
                VALUES
                    ($type, $no, $company, $tax, $date, $sub, $taxtotal, $total, $currency, $raw, $source, $created)
                RETURNING Id;
                """;
            BindDocument(insert, document, createdAt);
            var id = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            await InsertLinesAsync(connection, (SqliteTransaction)transaction, id, document.Lines, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            document.Id = id;
            document.CreatedAt = createdAt;
            return document;
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<FiscalDocumentDto> UpdateAsync(FiscalDocumentDto document, CancellationToken cancellationToken)
    {
        if (document.Id is not > 0)
            throw new InvalidOperationException("Güncellenecek kayıt bulunamadı.");

        await _write.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE Documents SET
                    DocumentType = $type,
                    DocumentNo = $no,
                    Company = $company,
                    TaxNumber = $tax,
                    DocumentDate = $date,
                    SubTotal = $sub,
                    TaxTotal = $taxtotal,
                    Total = $total,
                    Currency = $currency,
                    RawText = $raw,
                    SourceFileName = $source
                WHERE Id = $id;
                """;
            BindDocument(update, document, null);
            update.Parameters.AddWithValue("$id", document.Id.Value);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
                throw new InvalidOperationException("Kayıt veritabanında bulunamadı.");

            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM DocumentLines WHERE DocumentId = $id;";
            delete.Parameters.AddWithValue("$id", document.Id.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await InsertLinesAsync(connection, (SqliteTransaction)transaction, document.Id.Value, document.Lines, cancellationToken);

            await using var created = connection.CreateCommand();
            created.Transaction = (SqliteTransaction)transaction;
            created.CommandText = "SELECT CreatedAt FROM Documents WHERE Id = $id;";
            created.Parameters.AddWithValue("$id", document.Id.Value);
            document.CreatedAt = (string?)await created.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return document;
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Documents WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<FiscalDocumentDto?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var list = await ListAsync(null, null, cancellationToken, id);
        return list.FirstOrDefault();
    }

    public Task<IReadOnlyList<FiscalDocumentDto>> ListAsync(string? query, string? documentType, CancellationToken cancellationToken) =>
        ListAsync(query, documentType, cancellationToken, null);

    private async Task<IReadOnlyList<FiscalDocumentDto>> ListAsync(
        string? query,
        string? documentType,
        CancellationToken cancellationToken,
        long? id)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (id is > 0)
        {
            filters.Add("Id = $id");
            command.Parameters.AddWithValue("$id", id.Value);
        }

        if (!string.IsNullOrWhiteSpace(documentType) && documentType != "Tümü")
        {
            filters.Add("DocumentType = $dtype");
            command.Parameters.AddWithValue("$dtype", documentType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            filters.Add("(Company LIKE $q OR IFNULL(DocumentNo,'') LIKE $q OR IFNULL(TaxNumber,'') LIKE $q)");
            command.Parameters.AddWithValue("$q", "%" + query.Trim() + "%");
        }

        var where = filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters);
        command.CommandText = $"""
            SELECT Id, DocumentType, DocumentNo, Company, TaxNumber, DocumentDate, SubTotal, TaxTotal, Total, Currency, SourceFileName, CreatedAt, RawText
            FROM Documents
            {where}
            ORDER BY datetime(CreatedAt) DESC
            LIMIT 500;
            """;

        var documents = new List<FiscalDocumentDto>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(new FiscalDocumentDto
                {
                    Id = reader.GetInt64(0),
                    DocumentType = reader.GetString(1),
                    DocumentNo = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Company = reader.GetString(3),
                    TaxNumber = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Date = reader.IsDBNull(5) ? null : reader.GetString(5),
                    SubTotal = ParseDecimal(reader.IsDBNull(6) ? "0" : reader.GetString(6)),
                    TaxTotal = ParseDecimal(reader.IsDBNull(7) ? "0" : reader.GetString(7)),
                    Total = ParseDecimal(reader.GetString(8)),
                    Currency = reader.GetString(9),
                    SourceFileName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    CreatedAt = reader.GetString(11),
                    RawText = reader.IsDBNull(12) ? null : reader.GetString(12),
                    Lines = []
                });
            }
        }

        if (documents.Count == 0)
            return documents;

        await using var lines = connection.CreateCommand();
        var names = new string[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            names[i] = "$p" + i;
            lines.Parameters.AddWithValue(names[i], documents[i].Id!.Value);
        }

        lines.CommandText = $"""
            SELECT DocumentId, LineNo, Description, Quantity, Unit, UnitPrice, Amount
            FROM DocumentLines
            WHERE DocumentId IN ({string.Join(", ", names)})
            ORDER BY DocumentId, LineNo;
            """;

        var lookup = documents.ToDictionary(document => document.Id!.Value);
        await using var lineReader = await lines.ExecuteReaderAsync(cancellationToken);
        while (await lineReader.ReadAsync(cancellationToken))
        {
            var documentId = lineReader.GetInt64(0);
            lookup[documentId].Lines.Add(new FiscalLineDto
            {
                LineNo = lineReader.GetInt32(1),
                Description = lineReader.GetString(2),
                Quantity = ParseDecimal(lineReader.GetString(3)),
                Unit = lineReader.GetString(4),
                UnitPrice = ParseDecimal(lineReader.GetString(5)),
                Amount = ParseDecimal(lineReader.GetString(6))
            });
        }

        return documents;
    }

    private static async Task InsertLinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long documentId,
        IReadOnlyList<FiscalLineDto> items,
        CancellationToken cancellationToken)
    {
        foreach (var line in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO DocumentLines
                    (DocumentId, LineNo, Description, Quantity, Unit, UnitPrice, Amount)
                VALUES
                    ($doc, $no, $description, $qty, $unit, $price, $amount);
                """;
            command.Parameters.AddWithValue("$doc", documentId);
            command.Parameters.AddWithValue("$no", line.LineNo);
            command.Parameters.AddWithValue("$description", line.Description ?? "");
            command.Parameters.AddWithValue("$qty", FormatDecimal(line.Quantity));
            command.Parameters.AddWithValue("$unit", string.IsNullOrWhiteSpace(line.Unit) ? "AD" : line.Unit.Trim());
            command.Parameters.AddWithValue("$price", FormatDecimal(line.UnitPrice));
            command.Parameters.AddWithValue("$amount", FormatDecimal(line.Amount));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void BindDocument(SqliteCommand command, FiscalDocumentDto document, string? createdAt)
    {
        command.Parameters.AddWithValue("$type", string.IsNullOrWhiteSpace(document.DocumentType) ? "Belge" : document.DocumentType.Trim());
        command.Parameters.AddWithValue("$no", NullIfEmpty(document.DocumentNo));
        command.Parameters.AddWithValue("$company", document.Company.Trim());
        command.Parameters.AddWithValue("$tax", NullIfEmpty(document.TaxNumber));
        command.Parameters.AddWithValue("$date", NullIfEmpty(document.Date));
        command.Parameters.AddWithValue("$sub", FormatDecimal(document.SubTotal));
        command.Parameters.AddWithValue("$taxtotal", FormatDecimal(document.TaxTotal));
        command.Parameters.AddWithValue("$total", FormatDecimal(document.Total));
        command.Parameters.AddWithValue("$currency", string.IsNullOrWhiteSpace(document.Currency) ? "TRY" : document.Currency.Trim());
        command.Parameters.AddWithValue("$raw", NullIfEmpty(document.RawText));
        command.Parameters.AddWithValue("$source", NullIfEmpty(document.SourceFileName));
        if (createdAt is not null)
            command.Parameters.AddWithValue("$created", createdAt);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private static object NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
