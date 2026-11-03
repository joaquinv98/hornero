namespace BillingOss.Importer;

/// <summary>Una columna del CREATE TABLE: su nombre y su tipo tal como lo escribe el dump, por ejemplo <c>int(10) unsigned</c>.</summary>
public sealed record DumpColumn(string Name, string Type);

/// <summary>El esquema de una tabla: sus columnas en el orden del CREATE TABLE.</summary>
public sealed class DumpTable(string name, IReadOnlyList<DumpColumn> columns)
{
    public string Name { get; } = name;

    public IReadOnlyList<DumpColumn> Columns { get; } = columns;
}

/// <summary>
/// Una fila de un INSERT. Cada valor es <c>null</c> (NULL), <c>string</c> (texto, o número tal como se escribió,
/// sin perder precisión) o <c>byte[]</c> (binario: <c>0x...</c>, <c>_binary '...'</c>, <c>b'...'</c>).
/// </summary>
public sealed class DumpRow(DumpTable table, IReadOnlyList<DumpColumn> columns, object?[] values)
{
    public DumpTable Table { get; } = table;

    /// <summary>Las columnas del INSERT (todas las de la tabla si no traía lista), en el orden de <see cref="Values"/>.</summary>
    public IReadOnlyList<DumpColumn> Columns { get; } = columns;

    public IReadOnlyList<object?> Values { get; } = values;

    /// <summary>El valor de una columna por nombre (MySQL no distingue mayúsculas).</summary>
    public object? this[string column]
    {
        get
        {
            for (var i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i].Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return Values[i];
                }
            }

            throw new KeyNotFoundException($"La fila de {Table.Name} no trae la columna {column}.");
        }
    }
}

/// <summary>El dump no tiene la forma esperada o usa algo que el lector no soporta. El mensaje lleva el byte donde falló.</summary>
public sealed class DumpFormatException(string message) : FormatException(message);
