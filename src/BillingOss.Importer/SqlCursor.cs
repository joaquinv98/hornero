using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace BillingOss.Importer;

/// <summary>Faltan bytes para terminar lo que se estaba leyendo: el lector trae más del archivo y reintenta.</summary>
internal sealed class NeedMoreData : Exception;

/// <summary>
/// Lee SQL de un dump sobre los bytes que ya están en el búfer. Si se acaban antes de terminar la unidad y todavía
/// queda archivo, lanza <see cref="NeedMoreData"/>; con <c>final</c> el fin de los bytes es el fin del archivo.
/// Los comentarios, también los condicionales <c>/*! ... */</c>, se saltean enteros. Trabaja en bytes: el texto se
/// decodifica como UTF-8 recién cuando se arma un valor.
/// </summary>
internal ref struct SqlCursor
{
    // ponytail: todo el texto se decodifica como UTF-8; para dumps en latin1 u otros hay que leer el SET NAMES y elegir la codificación.
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SearchValues<byte> HexDigits = SearchValues.Create("0123456789abcdefABCDEF"u8);

    private readonly ReadOnlySpan<byte> data;
    private readonly bool final;
    private readonly byte[] delimiter;
    private readonly long offset;

    public int Position;

    public SqlCursor(ReadOnlySpan<byte> data, bool final, byte[] delimiter, long offset)
    {
        this.data = data;
        this.final = final;
        this.delimiter = delimiter;
        this.offset = offset;
    }

    public readonly DumpFormatException Error(string message, int? at = null) =>
        new($"{message} (byte {offset + (at ?? Position)}).");

    /// <summary>Lo que corresponde cuando los bytes se acaban en medio de algo: pedir más, o error si ya es el fin del archivo.</summary>
    private readonly Exception Truncated(string message) => final ? Error(message) : new NeedMoreData();

    public static bool IsWord(ReadOnlySpan<byte> word, ReadOnlySpan<byte> keyword) => Ascii.EqualsIgnoreCase(word, keyword);

    /// <summary>El byte en Position + ahead, o -1 en el fin del archivo.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Peek(int ahead = 0)
    {
        var i = Position + ahead;
        return i < data.Length ? data[i] : final ? -1 : throw new NeedMoreData();
    }

    public int Next()
    {
        var c = Peek();
        if (c >= 0)
        {
            Position++;
        }

        return c;
    }

    public void Expect(char c)
    {
        if (Peek() != c)
        {
            throw Error($"Se esperaba '{c}'");
        }

        Position++;
    }

    private static bool IsWordByte(int c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '$' or >= 0x80;

    /// <summary>La palabra (letras, dígitos, guion bajo) que empieza en Position, sin consumirla.</summary>
    public readonly ReadOnlySpan<byte> PeekWord()
    {
        var i = Position;
        while (i < data.Length && IsWordByte(data[i]))
        {
            i++;
        }

        return i == data.Length && !final ? throw new NeedMoreData() : data[Position..i];
    }

    /// <summary>Saltea espacios y comentarios: <c>-- </c>, <c>#</c> y <c>/* ... */</c>.</summary>
    public void SkipTrivia()
    {
        while (true)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                Position++;
            }
            else if (c == '#' || (c == '-' && Peek(1) == '-' && Peek(2) is -1 or ' ' or '\t' or '\n' or '\r'))
            {
                var eol = data[Position..].IndexOf((byte)'\n');
                if (eol >= 0)
                {
                    Position += eol + 1;
                }
                else if (final)
                {
                    Position = data.Length;
                }
                else
                {
                    throw new NeedMoreData();
                }
            }
            else if (c == '/' && Peek(1) == '*')
            {
                var close = data[(Position + 2)..].IndexOf("*/"u8);
                if (close >= 0)
                {
                    Position += close + 4;
                }
                else
                {
                    throw Truncated("Comentario sin cerrar");
                }
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>¿Está el delimitador de sentencias en Position?</summary>
    public readonly bool AtDelimiter()
    {
        if (data.Length - Position < delimiter.Length)
        {
            return final ? false : throw new NeedMoreData();
        }

        return data.Slice(Position, delimiter.Length).SequenceEqual(delimiter);
    }

    /// <summary>Saltea el resto de la sentencia, delimitador incluido. Un delimitador que falta en el último tramo se acepta.</summary>
    public void SkipStatement()
    {
        while (true)
        {
            SkipTrivia();
            var c = Peek();
            if (c < 0)
            {
                return;
            }

            if (AtDelimiter())
            {
                Position += delimiter.Length;
                return;
            }

            if (c is '\'' or '"' or '`')
            {
                ScanQuoted(c != '`', out _);
            }
            else
            {
                Position++;
            }
        }
    }

    /// <summary>Saltea hasta la coma o el paréntesis de cierre que terminan un elemento de la lista del CREATE TABLE, sin consumirlos.</summary>
    public void SkipItem()
    {
        while (true)
        {
            SkipTrivia();
            var c = Peek();
            if (c < 0)
            {
                throw Error("El archivo termina dentro del CREATE TABLE");
            }

            if (c is ',' or ')')
            {
                return;
            }

            if (c == '(')
            {
                SkipParentheses();
            }
            else if (c is '\'' or '"' or '`')
            {
                ScanQuoted(c != '`', out _);
            }
            else
            {
                Position++;
            }
        }
    }

    private void SkipParentheses()
    {
        Position++;
        while (true)
        {
            SkipTrivia();
            var c = Peek();
            if (c < 0)
            {
                throw Error("Paréntesis sin cerrar");
            }

            if (c == ')')
            {
                Position++;
                return;
            }

            if (c == '(')
            {
                SkipParentheses();
            }
            else if (c is '\'' or '"' or '`')
            {
                ScanQuoted(c != '`', out _);
            }
            else
            {
                Position++;
            }
        }
    }

    /// <summary>
    /// Con Position en la comilla de apertura, devuelve el contenido sin resolver los escapes y deja Position
    /// después de la comilla de cierre. <paramref name="escaped"/> dice si había barras o comillas dobles.
    /// </summary>
    public ReadOnlySpan<byte> ScanQuoted(bool backslashEscapes, out bool escaped)
    {
        var quote = data[Position];
        var from = Position + 1;
        var i = from;
        escaped = false;
        while (true)
        {
            var rest = data[i..];
            var j = backslashEscapes ? rest.IndexOfAny(quote, (byte)'\\') : rest.IndexOf(quote);
            if (j < 0)
            {
                throw Truncated("Cadena sin cerrar");
            }

            i += j;
            if (data[i] == '\\')
            {
                if (i + 1 >= data.Length)
                {
                    throw Truncated("Cadena sin cerrar");
                }

                escaped = true;
                i += 2;
                continue;
            }

            if (i + 1 >= data.Length && !final)
            {
                throw new NeedMoreData();
            }

            if (i + 1 < data.Length && data[i + 1] == quote)
            {
                escaped = true;
                i += 2;
                continue;
            }

            Position = i + 1;
            return data[from..i];
        }
    }

    /// <summary>Resuelve los escapes de MySQL: <c>\0 \b \n \r \t \Z</c>, <c>\%</c> y <c>\_</c> (que conservan la barra), la barra delante de cualquier otro byte y la comilla doble.</summary>
    private static ReadOnlySpan<byte> Unescape(ReadOnlySpan<byte> raw, byte quote)
    {
        var result = new byte[raw.Length];
        var n = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b == '\\' && i + 1 < raw.Length)
            {
                var e = raw[++i];
                if (e is (byte)'%' or (byte)'_')
                {
                    result[n++] = (byte)'\\';
                }

                result[n++] = (byte)(e switch
                {
                    (byte)'0' => 0,
                    (byte)'b' => 8,
                    (byte)'n' => '\n',
                    (byte)'r' => '\r',
                    (byte)'t' => '\t',
                    (byte)'Z' => 26,
                    _ => e,
                });
            }
            else if (b == quote && i + 1 < raw.Length && raw[i + 1] == quote)
            {
                result[n++] = quote;
                i++;
            }
            else
            {
                result[n++] = b;
            }
        }

        return result.AsSpan(0, n);
    }

    private readonly string Text(ReadOnlySpan<byte> bytes, int at)
    {
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Error("El texto no es UTF-8 válido", at);
        }
    }

    /// <summary>Un nombre de tabla o columna: con comillas invertidas (o dobles) o suelto. <c>base.tabla</c> devuelve <c>tabla</c>.</summary>
    public string ReadIdentifier()
    {
        var name = ReadIdentifierPart();
        while (Peek() == '.')
        {
            Position++;
            name = ReadIdentifierPart();
        }

        return name;
    }

    private string ReadIdentifierPart()
    {
        var at = Position;
        var c = Peek();
        if (c is '`' or '"')
        {
            var name = Text(ScanQuoted(false, out var doubled), at);
            return doubled ? name.Replace(new string((char)c, 2), ((char)c).ToString(), StringComparison.Ordinal) : name;
        }

        var word = PeekWord();
        if (word.IsEmpty)
        {
            throw Error("Se esperaba un nombre");
        }

        Position += word.Length;
        return Text(word, at);
    }

    /// <summary>El tipo de una columna: la palabra, sus argumentos y <c>unsigned</c>/<c>signed</c>/<c>zerofill</c>, como los escribe el dump.</summary>
    public string ReadType()
    {
        SkipTrivia();
        var from = Position;
        var word = PeekWord();
        if (word.IsEmpty)
        {
            throw Error("Se esperaba el tipo de la columna");
        }

        Position += word.Length;
        var end = Position;
        SkipTrivia();
        if (Peek() == '(')
        {
            SkipParentheses();
            end = Position;
        }

        while (true)
        {
            SkipTrivia();
            word = PeekWord();
            if (!(IsWord(word, "unsigned"u8) || IsWord(word, "signed"u8) || IsWord(word, "zerofill"u8)))
            {
                break;
            }

            Position += word.Length;
            end = Position;
        }

        return Text(data[from..end], from);
    }

    /// <summary>El argumento de <c>DELIMITER</c>: el resto de la línea.</summary>
    public byte[] ReadDelimiter()
    {
        var rest = data[Position..];
        var eol = rest.IndexOf((byte)'\n');
        if (eol < 0 && !final)
        {
            throw new NeedMoreData();
        }

        var text = (eol < 0 ? rest : rest[..eol]).Trim(" \t\r"u8);
        if (text.IsEmpty)
        {
            throw Error("DELIMITER sin delimitador");
        }

        Position += eol < 0 ? rest.Length : eol + 1;
        return text.ToArray();
    }

    /// <summary>Un valor de un INSERT: <c>null</c>, <c>string</c> (texto o número) o <c>byte[]</c>.</summary>
    public object? ReadValue()
    {
        var c = Peek();
        if (c is '\'' or '"')
        {
            return ReadText();
        }

        if (c == '_')
        {
            return ReadIntroduced();
        }

        if (c is (>= '0' and <= '9') or '+' or '-' or '.')
        {
            if (c == '0' && Peek(1) is 'x' or 'X')
            {
                return ReadHex();
            }

            return ReadNumber();
        }

        if (IsWordByte(c))
        {
            var word = PeekWord();
            if (IsWord(word, "null"u8))
            {
                Position += word.Length;
                return null;
            }

            if (word.Length == 1 && Peek(1) == '\'')
            {
                if (word[0] is (byte)'x' or (byte)'X')
                {
                    return ReadQuotedHex();
                }

                if (word[0] is (byte)'b' or (byte)'B')
                {
                    return ReadQuotedBits();
                }
            }
        }

        throw Error(c < 0 ? "El archivo termina dentro de un INSERT" : "Valor no soportado");
    }

    private string ReadText()
    {
        var at = Position;
        var quote = data[Position];
        var raw = ScanQuoted(true, out var escaped);
        return Text(escaped ? Unescape(raw, quote) : raw, at);
    }

    private byte[] ReadBytes()
    {
        var quote = data[Position];
        var raw = ScanQuoted(true, out var escaped);
        return (escaped ? Unescape(raw, quote) : raw).ToArray();
    }

    /// <summary>Introductor de juego de caracteres: <c>_binary</c> da bytes; <c>_utf8</c>, <c>_utf8mb3</c>, <c>_utf8mb4</c> y <c>_ascii</c> dan texto.</summary>
    private object ReadIntroduced()
    {
        var at = Position;
        var word = PeekWord();
        var binary = IsWord(word, "_binary"u8);
        if (!binary && !(IsWord(word, "_utf8"u8) || IsWord(word, "_utf8mb3"u8) || IsWord(word, "_utf8mb4"u8) || IsWord(word, "_ascii"u8)))
        {
            throw Error("Introductor de juego de caracteres no soportado", at);
        }

        Position += word.Length;
        SkipTrivia();
        var c = Peek();
        if (c is '\'' or '"')
        {
            return binary ? ReadBytes() : ReadText();
        }

        if (c == '0' && Peek(1) is 'x' or 'X')
        {
            var bytes = ReadHex();
            return binary ? bytes : Text(bytes, at);
        }

        throw Error("Después del introductor se esperaba una cadena");
    }

    /// <summary>Número tal como se escribió (entero, decimal o con exponente), para no perder precisión.</summary>
    private string ReadNumber()
    {
        var at = Position;
        var n = Peek() is '+' or '-' ? 1 : 0;
        var digits = 0;
        while (Peek(n) is >= '0' and <= '9')
        {
            n++;
            digits++;
        }

        if (Peek(n) == '.')
        {
            n++;
            while (Peek(n) is >= '0' and <= '9')
            {
                n++;
                digits++;
            }
        }

        if (digits > 0 && Peek(n) is 'e' or 'E')
        {
            n++;
            if (Peek(n) is '+' or '-')
            {
                n++;
            }

            var exponent = 0;
            while (Peek(n) is >= '0' and <= '9')
            {
                n++;
                exponent++;
            }

            if (exponent == 0)
            {
                digits = 0;
            }
        }

        if (digits == 0 || IsWordByte(Peek(n)))
        {
            throw Error("Número ilegible", at);
        }

        var text = Text(data.Slice(Position, n), at);
        Position += n;
        return text;
    }

    /// <summary><c>0x...</c>; con cantidad impar de dígitos se completa con un cero a la izquierda, como MySQL.</summary>
    private byte[] ReadHex()
    {
        var at = Position;
        var from = Position + 2;
        var rest = data[from..];
        var n = rest.IndexOfAnyExcept(HexDigits);
        if (n < 0)
        {
            n = final ? rest.Length : throw new NeedMoreData();
        }

        Position = from + n;
        if (n == 0 || IsWordByte(Peek()))
        {
            throw Error("Hexadecimal ilegible", at);
        }

        return FromHex(rest[..n]);
    }

    private byte[] ReadQuotedHex()
    {
        var at = Position;
        Position++;
        var digits = ScanQuoted(false, out _);
        return digits.ContainsAnyExcept(HexDigits) ? throw Error("Hexadecimal ilegible", at) : FromHex(digits);
    }

    /// <summary><c>b'101'</c>, como mysqldump escribe las columnas BIT: los bits justificados a la derecha en la menor cantidad de bytes.</summary>
    private byte[] ReadQuotedBits()
    {
        var at = Position;
        Position++;
        var bits = ScanQuoted(false, out _);
        if (bits.ContainsAnyExcept((byte)'0', (byte)'1'))
        {
            throw Error("Literal de bits ilegible", at);
        }

        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[bits.Length - 1 - i] == '1')
            {
                bytes[bytes.Length - 1 - (i / 8)] |= (byte)(1 << (i % 8));
            }
        }

        return bytes;
    }

    private static byte[] FromHex(ReadOnlySpan<byte> digits)
    {
        var bytes = new byte[(digits.Length + 1) / 2];
        var i = 0;
        var j = 0;
        if ((digits.Length & 1) == 1)
        {
            bytes[j++] = (byte)HexValue(digits[i++]);
        }

        while (i < digits.Length)
        {
            bytes[j++] = (byte)((HexValue(digits[i]) << 4) | HexValue(digits[i + 1]));
            i += 2;
        }

        return bytes;
    }

    private static int HexValue(byte c) => (c & 0xF) + (c > '9' ? 9 : 0);
}
