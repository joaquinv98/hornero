using System.Data;
using System.Security.Cryptography;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BillingOss.App.Content;

/// <summary>Un pedido que el módulo rechaza con un código HTTP propio: 413 (tamaño) o 415 (tipo).</summary>
public sealed class ContentRejectedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Dónde viven los bytes de las descargas (D-14). La implementación de fábrica los guarda en PostgreSQL, que los replica
/// con la base. Un almacenamiento compatible con S3 entra implementando esta misma interfaz.
/// </summary>
public interface IContentFileStore
{
    /// <summary>Guarda los bytes de la descarga, reemplazando los que hubiera. Corre en la transacción del que llama.</summary>
    Task Save(long downloadId, byte[] bytes, CancellationToken ct);

    /// <summary>Abre los bytes para leerlos de a poco, o nulo si no hay. El que llama cierra el flujo.</summary>
    Task<Stream?> Open(long downloadId, CancellationToken ct);
}

/// <summary>Los bytes en <c>content.download_files</c>. Se leen en flujo, sin cargar el archivo entero en memoria.</summary>
public sealed class DatabaseFileStore(BillingDbContext db) : IContentFileStore
{
    public async Task Save(long downloadId, byte[] bytes, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO content.download_files (download_id, bytes) VALUES ({downloadId}, {bytes})
            ON CONFLICT (download_id) DO UPDATE SET bytes = EXCLUDED.bytes
            """, ct).ConfigureAwait(false);

    /// <summary>
    /// Cada flujo abre su propia conexión del pool: mientras se lee el archivo la del contexto queda libre para contar la
    /// descarga, y dos descargas a la vez no se pisan. La conexión vuelve al pool cuando se cierra el flujo.
    /// </summary>
    public async Task<Stream?> Open(long downloadId, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(db.Database.GetConnectionString() ?? throw new InvalidOperationException("Los archivos de las descargas necesitan PostgreSQL."));
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            command = new NpgsqlCommand("SELECT bytes FROM content.download_files WHERE download_id = @id", connection);
            command.Parameters.AddWithValue("id", downloadId);
            reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new OwnedStream(reader.GetStream(0), reader, command, connection);
            }
        }
        catch
        {
            await Release(reader, command, connection).ConfigureAwait(false);
            throw;
        }

        await Release(reader, command, connection).ConfigureAwait(false);
        return null;
    }

    private static async Task Release(NpgsqlDataReader? reader, NpgsqlCommand? command, NpgsqlConnection connection)
    {
        if (reader is not null)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        if (command is not null)
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Un flujo que al cerrarse cierra también el lector, el comando y la conexión que lo sostienen.</summary>
    private sealed class OwnedStream(Stream inner, NpgsqlDataReader reader, NpgsqlCommand command, NpgsqlConnection connection) : Stream
    {
        private bool disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
                await Release(reader, command, connection).ConfigureAwait(false);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                inner.Dispose();
                reader.Dispose();
                command.Dispose();
                connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

public sealed record DownloadInput(long CategoryId, int SortOrder, ContentAudience Audience, long[] ProductIds, long[] GroupIds, IReadOnlyDictionary<string, ContentText> Texts, int? Revision = null);

public sealed record DownloadRow(Download Download, IReadOnlyDictionary<string, ContentText> Texts);

/// <summary>Una descarga como la ve el cliente.</summary>
public sealed record DownloadCard(long Id, string Title, string? Description, string FileName, long Size, string Sha256, long Count, DateTimeOffset UpdatedAt, string Language, bool Fallback);

/// <summary>Las descargas de una categoría, con la ruta de la categoría como título ("Instaladores / Windows").</summary>
public sealed record DownloadGroup(long CategoryId, string Title, IReadOnlyList<DownloadCard> Items);

/// <summary>Un archivo abierto para enviarlo. El que lo recibe cierra <see cref="Content"/>.</summary>
public sealed record DownloadFileStream(Stream Content, string FileName, string ContentType, long Size);

public sealed record UploadedFile(string FileName, string ContentType, long Size, string Sha256);

/// <summary>Descargas (sección 5 de diseno/contenidos.md): archivos por categoría con audiencia, contador, tope de tamaño y tipos permitidos.</summary>
public sealed class DownloadService(BillingDbContext db, TimeProvider time, AuditLog audit, IContentFileStore store, IOptions<ContentOptions> options)
{
    /// <summary>Publicadas, con archivo y visibles para quien mira, con alias x.</summary>
    private const string VisibleSql = "SELECT x.* FROM content.downloads x WHERE x.status = 'Published' AND x.size IS NOT NULL AND " + ContentViewer.Predicate;

    private IQueryable<Download> Published(ContentViewer viewer) => db.Set<Download>().FromSqlRaw(VisibleSql, ContentDb.Params(viewer)).AsNoTracking();

    // ---- Staff ----

    public async Task<long> Create(DownloadInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = await Check(input, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var download = new Download { CreatedAt = now, UpdatedAt = now };
        Fill(download, input, texts);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(download);
        await ContentDb.Save(db, "la descarga", ct).ConfigureAwait(false);
        await audit.Record("content.download.create", $"download:{download.Id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return download.Id;
    }

    public async Task Update(long id, DownloadInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = await Check(input, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var download = await db.Set<Download>().SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la descarga {id}.");
        ContentDb.CheckRevision(input.Revision, download.Revision, "la descarga");
        Fill(download, input, texts);
        Touch(download);
        await ContentDb.Save(db, "la descarga", ct).ConfigureAwait(false);
        await audit.Record("content.download.update", $"download:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Guarda el archivo de la descarga, reemplazando el que tenga. Rechaza el que pasa el tope (413) y el de una extensión no
    /// permitida (415), antes de tocar nada. Calcula el SHA-256 para que el cliente pueda verificar lo que baja.
    /// </summary>
    public async Task<UploadedFile> Upload(long id, string? fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = DownloadRules.CleanName(fileName);
        var extension = DownloadRules.Extension(name);
        if (name.Length == 0 || extension.Length == 0)
        {
            throw new ValidationException("El archivo necesita un nombre con extensión, por ejemplo agente.zip.");
        }

        if (!options.Value.Extensions.Contains(extension))
        {
            throw new ContentRejectedException(UnsupportedType, $"No se aceptan archivos .{extension}. Los permitidos son {string.Join(", ", options.Value.Extensions.Order())}.");
        }

        if (!await db.Set<Download>().AnyAsync(d => d.Id == id, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la descarga {id}.");
        }

        var max = options.Value.MaxDownloadBytes;
        var bytes = await ReadBounded(content, max, ct).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new ValidationException("El archivo está vacío.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var download = await db.Set<Download>().SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la descarga {id}.");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await store.Save(id, bytes, ct).ConfigureAwait(false);
        download.FileName = name;
        download.ContentType = DownloadRules.ContentType(extension);
        download.Size = bytes.Length;
        download.Sha256 = sha;
        Touch(download);
        await ContentDb.Save(db, "la descarga", ct).ConfigureAwait(false);
        await audit.Record("content.download.upload", $"download:{id}", new { name, size = bytes.Length, sha256 = sha }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new UploadedFile(name, download.ContentType, bytes.Length, sha);
    }

    private const int UnsupportedType = 415;

    private const int TooLarge = 413;

    // ponytail: el archivo entero en memoria (hasta MaxDownloadBytes) y un solo INSERT. Pasar a un flujo por bloques o al
    // almacenamiento externo (IContentFileStore) si el tope sube mucho o las subidas pasan de unas pocas a la vez.
    private static async Task<byte[]> ReadBounded(Stream content, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new ContentRejectedException(TooLarge, $"El archivo pasa el tope de {max / (1024 * 1024)} MiB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Publicar necesita el archivo: sin él la descarga no tiene qué dar.</summary>
    public Task Publish(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Published, "publish", revision, ct);

    public Task Unpublish(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Draft, "unpublish", revision, ct);

    public Task Archive(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Archived, "archive", revision, ct);

    public async Task Delete(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (await db.Set<Download>().Where(d => d.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"No existe la descarga {id}.");
        }

        await audit.Record("content.download.delete", $"download:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<DownloadRow?> Get(long id, CancellationToken ct = default)
    {
        var download = await db.Set<Download>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        return download is null ? null : new DownloadRow(download, ContentTexts.Parse(download.Texts));
    }

    public async Task<IReadOnlyList<DownloadRow>> List(long? categoryId = null, PublishState? status = null, int limit = 500, CancellationToken ct = default)
    {
        var query = db.Set<Download>().AsNoTracking();
        if (categoryId is { } category)
        {
            query = query.Where(d => d.CategoryId == category);
        }

        if (status is { } state)
        {
            query = query.Where(d => d.Status == state);
        }

        var rows = await query.OrderBy(d => d.CategoryId).ThenBy(d => d.SortOrder).ThenBy(d => d.Id).Take(Math.Clamp(limit, 1, 2000)).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(d => new DownloadRow(d, ContentTexts.Parse(d.Texts)))];
    }

    /// <summary>El archivo para el staff, sin sumar al contador.</summary>
    public async Task<DownloadFileStream?> OpenForStaff(long id, CancellationToken ct = default)
    {
        var download = await db.Set<Download>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.Size != null, ct).ConfigureAwait(false);
        return download is null ? null : await Open(download, ct).ConfigureAwait(false);
    }

    // ---- Quien lee ----

    /// <summary>Lo que quien mira puede bajar, por categoría. Las categorías ocultas o sin nada visible no aparecen.</summary>
    public async Task<IReadOnlyList<DownloadGroup>> Visible(ContentViewer viewer, string? language, CancellationToken ct = default)
    {
        var downloads = await Published(viewer).OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        if (downloads.Count == 0)
        {
            return [];
        }

        var categories = await db.Set<ContentCategory>().AsNoTracking().Where(c => c.Kind == CategoryKind.Downloads).ToListAsync(ct).ConfigureAwait(false);
        var byId = categories.ToDictionary(c => c.Id);
        string Path(ContentCategory c) => string.Join(" / ", Chain(c).Select(p => ContentTexts.Pick(ContentTexts.Parse(p.Texts), language)?.Title ?? p.Slug));
        IEnumerable<ContentCategory> Chain(ContentCategory c)
        {
            var chain = new List<ContentCategory> { c };
            while (chain[0].ParentId is { } up && byId.TryGetValue(up, out var parent) && chain.Count < 8)
            {
                chain.Insert(0, parent);
            }

            return chain;
        }

        var groups = new List<DownloadGroup>();
        foreach (var category in categories.Where(c => !Chain(c).Any(p => p.Hidden)).OrderBy(c => Path(c), StringComparer.CurrentCultureIgnoreCase))
        {
            var items = downloads.Where(d => d.CategoryId == category.Id).Select(d => Card(d, language)).OfType<DownloadCard>().ToList();
            if (items.Count > 0)
            {
                groups.Add(new DownloadGroup(category.Id, Path(category), items));
            }
        }

        return groups;
    }

    /// <summary>Los datos del archivo que quien mira puede bajar, sin abrirlo ni sumar al contador: para responder un HEAD.</summary>
    public async Task<UploadedFile?> Describe(ContentViewer viewer, long id, CancellationToken ct = default)
    {
        var download = await Published(viewer).SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        return download is { FileName: not null, ContentType: not null, Size: not null, Sha256: not null }
            ? new UploadedFile(download.FileName, download.ContentType, download.Size.Value, download.Sha256)
            : null;
    }

    /// <summary>
    /// Abre el archivo para enviarlo y suma uno al contador. Nulo si no existe, no está publicado o no corresponde a quien
    /// pide: igual que lo ajeno, da 404.
    /// </summary>
    public async Task<DownloadFileStream?> Open(ContentViewer viewer, long id, CancellationToken ct = default)
    {
        var download = await Published(viewer).SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (download is null)
        {
            return null;
        }

        var file = await Open(download, ct).ConfigureAwait(false);
        if (file is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE content.downloads SET download_count = download_count + 1 WHERE id = {id}", ct).ConfigureAwait(false);
        }

        return file;
    }

    // ---- Internos ----

    private async Task<DownloadFileStream?> Open(Download download, CancellationToken ct)
    {
        var content = await store.Open(download.Id, ct).ConfigureAwait(false);
        return content is null ? null : new DownloadFileStream(content, download.FileName ?? $"descarga-{download.Id}", download.ContentType ?? "application/octet-stream", download.Size ?? 0);
    }

    private static DownloadCard? Card(Download d, string? language)
    {
        var picked = ContentTexts.PickWithLanguage(ContentTexts.Parse(d.Texts), language);
        return picked is not { } found
            ? null
            : new DownloadCard(d.Id, found.Text.Title, found.Text.Summary, d.FileName ?? string.Empty, d.Size ?? 0, d.Sha256 ?? string.Empty, d.DownloadCount, d.UpdatedAt, found.Language, found.Language != ContentTexts.Reader(language));
    }

    private async Task<Dictionary<string, ContentText>> Check(DownloadInput input, CancellationToken ct)
    {
        var texts = ContentTexts.Clean(input.Texts ?? new Dictionary<string, ContentText>());
        if (ContentTexts.Error(texts) is { } error)
        {
            throw new ValidationException(error);
        }

        var kind = await db.Set<ContentCategory>().AsNoTracking().Where(c => c.Id == input.CategoryId).Select(c => (CategoryKind?)c.Kind).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (kind != CategoryKind.Downloads)
        {
            throw new ValidationException($"No existe la categoría {input.CategoryId} de descargas.");
        }

        await ContentDb.CheckAudience(db, input.Audience, input.ProductIds ?? [], input.GroupIds ?? [], ct).ConfigureAwait(false);
        return texts;
    }

    private static void Fill(Download download, DownloadInput input, Dictionary<string, ContentText> texts)
    {
        download.CategoryId = input.CategoryId;
        download.SortOrder = input.SortOrder;
        download.Audience = input.Audience;
        download.ProductIds = [.. input.ProductIds.Distinct().Order()];
        download.GroupIds = [.. input.GroupIds.Distinct().Order()];
        download.Texts = ContentTexts.Serialize(texts);
    }

    private void Touch(Download download)
    {
        download.Revision++;
        download.UpdatedAt = time.GetUtcNow();
    }

    private async Task Move(long id, PublishState state, string action, int? revision, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var download = await db.Set<Download>().SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la descarga {id}.");
        ContentDb.CheckRevision(revision, download.Revision, "la descarga");
        if (state == PublishState.Published && !download.HasFile)
        {
            throw new ValidationException("La descarga no tiene archivo: subilo antes de publicarla.");
        }

        download.Status = state;
        Touch(download);
        await ContentDb.Save(db, "la descarga", ct).ConfigureAwait(false);
        await audit.Record($"content.download.{action}", $"download:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
