using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using static BillingOss.Importer.SqlCursor;

namespace BillingOss.Importer;

/// <summary>
/// Lector en streaming de un dump de mysqldump/mariadb-dump (<c>.sql</c> o <c>.sql.gz</c>): no levanta ningún
/// servidor y no carga el archivo entero. Entrega las filas de los INSERT a medida que las lee; el CREATE TABLE de
/// cada tabla tiene que venir antes de sus INSERT, como lo escribe mysqldump. Cada sentencia que no sea
/// CREATE TABLE ni INSERT/REPLACE se ignora. En memoria queda, como mucho, la sentencia o la fila en curso.
/// </summary>
/// <remarks>
/// Límites: texto solo en UTF-8 (estricto); una sola base por dump (sin USE); solo <c>INSERT ... VALUES</c> con
/// literales (sin expresiones ni funciones); los comentarios condicionales no se ejecutan.
/// </remarks>
public sealed class DumpReader : IAsyncDisposable
{
    /// <summary>Tope del búfer: una sentencia o una fila más grande que esto es un dump dañado (por ejemplo, una comilla sin cerrar).</summary>
    // ponytail: cada sentencia o fila tiene que entrar entera en memoria; para saltear tablas enormes sin decodificarlas, agregar un filtro por tabla.
    private const int MaxBuffer = 1 << 30;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    /// <summary>Palabras con las que empieza un elemento del CREATE TABLE que no es una columna.</summary>
    private static readonly HashSet<string> ConstraintWords = new(
        ["primary", "key", "unique", "index", "fulltext", "spatial", "constraint", "foreign", "check", "period", "vector", "like"],
        StringComparer.OrdinalIgnoreCase);

    private delegate T Parse<T>(ref SqlCursor cursor);

    /// <summary>El INSERT en curso: la tabla y las columnas a las que corresponden los valores.</summary>
    private sealed record Insert(DumpTable Table, IReadOnlyList<DumpColumn> Columns);

    private readonly Stream input;
    private readonly bool leaveOpen;
    private readonly int lowWater;
    private readonly Dictionary<string, DumpTable> tables = new(StringComparer.Ordinal);
    private readonly Parse<bool> readStatement;
    private readonly Parse<DumpRow> readRow;
    private byte[] buf;
    private int start;
    private int end;
    private long offset;
    private bool eof;
    private bool started;
    private byte[] delimiter = [(byte)';'];
    private Insert? insert;

    /// <summary>Lee SQL en bytes (UTF-8) de <paramref name="input"/>; si viene comprimido, hay que descomprimirlo antes.</summary>
    /// <param name="bufferSize">Tamaño inicial del búfer; crece si una sentencia o una fila no entra.</param>
    public DumpReader(Stream input, bool leaveOpen = false, int bufferSize = 1 << 20)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferSize, MaxBuffer);
        this.input = input;
        this.leaveOpen = leaveOpen;
        buf = new byte[bufferSize];
        lowWater = Math.Min(64 * 1024, bufferSize / 2);
        readStatement = ReadStatement;
        readRow = ReadRow;
    }

    /// <summary>Abre un archivo <c>.sql</c> o <c>.sql.gz</c>; lo comprimido se reconoce por los bytes, no por el nombre.</summary>
    public static DumpReader Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Span<byte> magic = stackalloc byte[2];
        var read = file.Read(magic);
        file.Position = 0;
        return new DumpReader(read == 2 && magic[0] == 0x1F && magic[1] == 0x8B ? new GZipStream(file, CompressionMode.Decompress) : file);
    }

    /// <summary>Las tablas con CREATE TABLE leídas hasta ahora (todas, al terminar de recorrer las filas).</summary>
    public IReadOnlyDictionary<string, DumpTable> Tables => tables;

    /// <summary>Recorre el dump una sola vez y devuelve las filas en el orden del archivo.</summary>
    public async IAsyncEnumerable<DumpRow> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (started)
        {
            throw new InvalidOperationException("El dump ya se leyó: hay que abrir otro DumpReader.");
        }

        started = true;
        await SkipBomAsync(cancellationToken);
        while (true)
        {
            if (!eof && end - start < lowWater)
            {
                await FillAsync(cancellationToken);
            }

            if (insert is null)
            {
                bool ended;
                while (!TryRun(readStatement, out ended))
                {
                    await FillAsync(cancellationToken);
                }

                if (ended)
                {
                    yield break;
                }
            }
            else
            {
                DumpRow? row;
                while (!TryRun(readRow, out row))
                {
                    await FillAsync(cancellationToken);
                }

                yield return row;
            }
        }
    }

    public ValueTask DisposeAsync() => leaveOpen ? ValueTask.CompletedTask : input.DisposeAsync();

    private async ValueTask SkipBomAsync(CancellationToken cancellationToken)
    {
        while (!eof && end - start < 3)
        {
            await FillAsync(cancellationToken);
        }

        var head = buf.AsSpan(start, end - start);
        if (head.StartsWith(Utf8Bom))
        {
            start += 3;
            offset += 3;
        }
        else if (head.StartsWith(Utf16LeBom) || head.StartsWith(Utf16BeBom))
        {
            throw new DumpFormatException("El dump está en UTF-16 (o UTF-32): hay que convertirlo a UTF-8 (byte 0).");
        }
    }

    /// <summary>Junta los bytes que quedan al principio del búfer y lo llena hasta el tope o el fin del archivo; si no hay lugar, lo duplica.</summary>
    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (eof)
        {
            throw new InvalidOperationException("Error interno: se pidieron más bytes después del fin del archivo.");
        }

        if (start > 0)
        {
            buf.AsSpan(start, end - start).CopyTo(buf);
            end -= start;
            start = 0;
        }

        if (end == buf.Length)
        {
            if (buf.Length >= MaxBuffer)
            {
                throw new DumpFormatException($"Una sentencia o una fila ocupa más de {MaxBuffer >> 20} MB (byte {offset}): el dump parece dañado.");
            }

            Array.Resize(ref buf, buf.Length * 2);
        }

        while (end < buf.Length)
        {
            var read = await input.ReadAsync(buf.AsMemory(end), cancellationToken);
            if (read == 0)
            {
                eof = true;
                return;
            }

            end += read;
        }
    }

    /// <summary>
    /// Interpreta una unidad (una sentencia, o una fila de un INSERT) sobre lo que hay en el búfer. Devuelve false si
    /// faltan bytes: no consume nada y hay que llenar el búfer y reintentar. Por eso cada unidad aplica sus efectos
    /// (tablas, INSERT en curso, delimitador) recién al final, cuando ya no puede faltarle nada.
    /// </summary>
    private bool TryRun<T>(Parse<T> parse, [MaybeNullWhen(false)] out T result)
    {
        var cursor = new SqlCursor(buf.AsSpan(start, end - start), eof, delimiter, offset);
        try
        {
            result = parse(ref cursor);
            start += cursor.Position;
            offset += cursor.Position;
            return true;
        }
        catch (NeedMoreData)
        {
            result = default;
            return false;
        }
    }

    /// <summary>Una sentencia: devuelve true en el fin del archivo.</summary>
    private bool ReadStatement(ref SqlCursor cur)
    {
        cur.SkipTrivia();
        if (cur.Peek() < 0)
        {
            return true;
        }

        if (cur.AtDelimiter())
        {
            cur.Position += delimiter.Length;
            return false;
        }

        var word = cur.PeekWord();
        if (IsWord(word, "insert"u8) || IsWord(word, "replace"u8))
        {
            cur.Position += word.Length;
            insert = ReadInsertHead(ref cur);
        }
        else if (IsWord(word, "create"u8))
        {
            cur.Position += word.Length;
            var table = ReadCreateTable(ref cur);
            cur.SkipStatement();
            if (table is not null)
            {
                tables[table.Name] = table;
            }
        }
        else if (IsWord(word, "delimiter"u8))
        {
            cur.Position += word.Length;
            delimiter = cur.ReadDelimiter();
        }
        else
        {
            cur.SkipStatement();
        }

        return false;
    }

    /// <summary>Con Position después de CREATE: las columnas si es una tabla, o null si es otra cosa (vista, trigger, base...).</summary>
    private static DumpTable? ReadCreateTable(ref SqlCursor cur)
    {
        cur.SkipTrivia();
        var word = cur.PeekWord();
        if (IsWord(word, "temporary"u8))
        {
            cur.Position += word.Length;
            cur.SkipTrivia();
            word = cur.PeekWord();
        }

        if (!IsWord(word, "table"u8))
        {
            return null;
        }

        cur.Position += word.Length;
        cur.SkipTrivia();
        if (IsWord(cur.PeekWord(), "if"u8))
        {
            for (var i = 0; i < 3; i++)
            {
                // IF NOT EXISTS
                cur.SkipTrivia();
                cur.Position += cur.PeekWord().Length;
            }
        }

        cur.SkipTrivia();
        var name = cur.ReadIdentifier();
        cur.SkipTrivia();
        if (cur.Peek() != '(')
        {
            return null; // CREATE TABLE ... LIKE / AS SELECT: no trae la lista de columnas
        }

        cur.Position++;
        var columns = new List<DumpColumn>();
        while (true)
        {
            cur.SkipTrivia();
            var c = cur.Peek();
            word = cur.PeekWord();
            if (c is '`' or '"' || (!word.IsEmpty && !ConstraintWords.Contains(Encoding.ASCII.GetString(word))))
            {
                columns.Add(new DumpColumn(cur.ReadIdentifier(), cur.ReadType()));
            }
            else if (word.IsEmpty)
            {
                throw cur.Error("Se esperaba una columna o una clave en el CREATE TABLE");
            }

            cur.SkipItem();
            c = cur.Next();
            if (c == ')')
            {
                return new DumpTable(name, columns);
            }

            if (c != ',')
            {
                throw cur.Error("Se esperaba , o ) en el CREATE TABLE");
            }
        }
    }

    /// <summary>Con Position después de INSERT/REPLACE: llega hasta VALUES, sin leer ninguna fila.</summary>
    private Insert ReadInsertHead(ref SqlCursor cur)
    {
        while (true)
        {
            cur.SkipTrivia();
            var word = cur.PeekWord();
            if (IsWord(word, "into"u8) || IsWord(word, "ignore"u8) || IsWord(word, "low_priority"u8)
                || IsWord(word, "high_priority"u8) || IsWord(word, "delayed"u8))
            {
                cur.Position += word.Length;
            }
            else
            {
                break;
            }
        }

        var name = cur.ReadIdentifier();
        if (!tables.TryGetValue(name, out var table))
        {
            throw cur.Error($"INSERT en la tabla {name}, que no tiene un CREATE TABLE antes");
        }

        IReadOnlyList<DumpColumn> columns = table.Columns;
        cur.SkipTrivia();
        if (cur.Peek() == '(')
        {
            cur.Position++;
            var listed = new List<DumpColumn>();
            while (true)
            {
                cur.SkipTrivia();
                var column = cur.ReadIdentifier();
                listed.Add(table.Columns.FirstOrDefault(col => string.Equals(col.Name, column, StringComparison.OrdinalIgnoreCase))
                    ?? throw cur.Error($"La tabla {name} no tiene la columna {column}"));
                cur.SkipTrivia();
                var c = cur.Next();
                if (c == ')')
                {
                    break;
                }

                if (c != ',')
                {
                    throw cur.Error("Se esperaba , o ) en la lista de columnas");
                }
            }

            columns = listed;
            cur.SkipTrivia();
        }

        var values = cur.PeekWord();
        if (!IsWord(values, "values"u8) && !IsWord(values, "value"u8))
        {
            throw cur.Error($"INSERT en {name} sin VALUES: solo se soporta INSERT ... VALUES");
        }

        cur.Position += values.Length;
        return new Insert(table, columns);
    }

    /// <summary>Una fila del INSERT en curso y lo que le sigue: otra fila o el fin de la sentencia.</summary>
    private DumpRow ReadRow(ref SqlCursor cur)
    {
        var current = insert!;
        var values = new object?[current.Columns.Count];
        var count = 0;
        try
        {
            cur.SkipTrivia();
            cur.Expect('(');
            while (true)
            {
                cur.SkipTrivia();
                var value = cur.ReadValue();
                if (count < values.Length)
                {
                    values[count] = value;
                }

                count++;
                cur.SkipTrivia();
                var c = cur.Next();
                if (c == ')')
                {
                    break;
                }

                if (c != ',')
                {
                    throw cur.Error("Se esperaba , o )");
                }
            }

            if (count != values.Length)
            {
                throw cur.Error($"La fila trae {count} valores y las columnas son {values.Length}");
            }

            cur.SkipTrivia();
            if (cur.Peek() == ',')
            {
                cur.Position++;
            }
            else if (cur.Peek() < 0)
            {
                insert = null;
            }
            else if (cur.AtDelimiter())
            {
                cur.Position += delimiter.Length;
                insert = null;
            }
            else
            {
                throw cur.Error("Después de la fila se esperaba , o el fin de la sentencia");
            }
        }
        catch (DumpFormatException e)
        {
            throw new DumpFormatException($"Tabla {current.Table.Name}: {e.Message}");
        }

        return new DumpRow(current.Table, current.Columns, values);
    }
}
