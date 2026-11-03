using System.IO.Compression;
using System.Text;

namespace BillingOss.Importer.Tests;

/// <summary>Dumps sintéticos con la forma de mysqldump/mariadb-dump. Ningún dato es real.</summary>
[TestClass]
public sealed class DumpReaderTests
{
    /// <summary>Se prueba cada dump con estos tamaños de búfer: los chicos obligan a cortar sentencias, filas y cadenas por la mitad.</summary>
    private static readonly int[] BufferSizes = [1, 2, 3, 5, 8, 13, 64, 1 << 20];

    private const string Header = """
        /*M!999999\- enable the sandbox mode */
        -- MariaDB dump 10.19-11.4.13-MariaDB, for debian-linux-gnu (x86_64)
        --
        -- Host: 127.0.0.1    Database: prueba
        -- ------------------------------------------------------
        /*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
        /*!40101 SET NAMES utf8 */;
        /*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
        /*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;

        """;

    private const string Sample = Header + """
        CREATE TABLE `t` (
          `id` int(11) NOT NULL,
          `v` text
        ) ENGINE=InnoDB;
        INSERT INTO `t` VALUES
        (1,'a\nb'),
        (2,'ñ');

        """;

    /// <summary>Un stream que entrega pocos bytes por lectura, como un archivo comprimido o una red.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }

    private sealed record Result(IReadOnlyDictionary<string, DumpTable> Tables, List<DumpRow> Rows);

    private static async Task<Result> ReadWith(byte[] sql, int bufferSize)
    {
        await using var dump = new DumpReader(new MemoryStream(sql), bufferSize: bufferSize);
        var rows = new List<DumpRow>();
        await foreach (var row in dump.ReadRowsAsync())
        {
            rows.Add(row);
        }

        return new Result(dump.Tables, rows);
    }

    /// <summary>Lee el dump con todos los tamaños de búfer, exige el mismo resultado en todos y devuelve el del búfer grande.</summary>
    private static async Task<Result> Read(byte[] sql)
    {
        var result = await ReadWith(sql, BufferSizes[^1]);
        var expected = Render(result);
        foreach (var size in BufferSizes[..^1])
        {
            Assert.AreEqual(expected, Render(await ReadWith(sql, size)), $"El resultado cambia con un búfer de {size} bytes.");
        }

        return result;
    }

    private static Task<Result> Read(string sql) => Read(Encoding.UTF8.GetBytes(sql));

    private static async Task<string> Fails(byte[] sql)
    {
        string? message = null;
        foreach (var size in BufferSizes)
        {
            var error = await Assert.ThrowsExactlyAsync<DumpFormatException>(() => ReadWith(sql, size));
            Assert.Contains("(byte ", error.Message);
            message ??= error.Message;
        }

        return message!;
    }

    private static Task<string> Fails(string sql) => Fails(Encoding.UTF8.GetBytes(sql));

    private static string Render(Result result)
    {
        var text = new StringBuilder();
        foreach (var table in result.Tables.Values)
        {
            text.Append(table.Name).Append('(').AppendJoin(", ", table.Columns.Select(c => $"{c.Name} {c.Type}")).AppendLine(")");
        }

        foreach (var row in result.Rows)
        {
            text.Append(row.Table.Name).Append('[').AppendJoin(',', row.Columns.Select(c => c.Name)).Append("] ");
            foreach (var value in row.Values)
            {
                text.Append(value switch { null => "NULL", string s => $"'{s}'", byte[] b => "0x" + Convert.ToHexString(b), _ => "?" }).Append(' ');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string?[] Texts(DumpRow row) => [.. row.Values.Cast<string?>()];

    [TestMethod]
    public async Task CreateTable_ColumnasEnOrdenConSusTipos()
    {
        var result = await Read(Header + """
            DROP TABLE IF EXISTS `clients`;
            /*!40101 SET @saved_cs_client     = @@character_set_client */;
            /*!40101 SET character_set_client = utf8mb4 */;
            CREATE TABLE `clients` (
              `id` int(10) unsigned NOT NULL AUTO_INCREMENT,
              `name` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'a,b (c)' COMMENT 'it''s; (not) a column',
              `balance` decimal(10,2) NOT NULL DEFAULT 0.00,
              `kind` enum('a,b','c)d','it''s') DEFAULT NULL,
              `flags` bigint(20) unsigned zerofill NOT NULL,
              `created` datetime(6) DEFAULT current_timestamp(6) ON UPDATE current_timestamp(6),
              `notes` text DEFAULT NULL,
              `blob` longblob,
              PRIMARY KEY (`id`),
              UNIQUE KEY `name` (`name`(20)),
              KEY `kind` (`kind`,`created`),
              FULLTEXT KEY `ft` (`notes`),
              VECTOR KEY `vk` (`notes`),
              CONSTRAINT `fk` FOREIGN KEY (`id`) REFERENCES `other` (`id`) ON DELETE CASCADE,
              CHECK (`balance` >= 0)
            ) ENGINE=InnoDB AUTO_INCREMENT=5 DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_unicode_ci COMMENT='a; b' /*!50100 PARTITION BY HASH (`id`) PARTITIONS 4 */;
            /*!40101 SET character_set_client = @saved_cs_client */;
            CREATE TABLE IF NOT EXISTS `other`(`id` int(11) NOT NULL);
            CREATE TABLE `copy` LIKE `other`;
            """);

        Assert.AreEqual(2, result.Tables.Count);
        var clients = result.Tables["clients"];
        CollectionAssert.AreEqual(
            new[] { "id", "name", "balance", "kind", "flags", "created", "notes", "blob" },
            clients.Columns.Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "int(10) unsigned",
                "varchar(64)",
                "decimal(10,2)",
                "enum('a,b','c)d','it''s')",
                "bigint(20) unsigned zerofill",
                "datetime(6)",
                "text",
                "longblob",
            },
            clients.Columns.Select(c => c.Type).ToArray());
        Assert.AreEqual(new DumpColumn("id", "int(11)"), result.Tables["other"].Columns.Single());
        Assert.AreEqual(0, result.Rows.Count);
    }

    [TestMethod]
    public async Task CreateTable_SinComillasYEnMinusculas()
    {
        var result = await Read("""
            create table bare (id int, name varchar(10), primary key (id), key k (name));
            insert into bare values (1,'x');
            """);

        CollectionAssert.AreEqual(new[] { "id", "name" }, result.Tables["bare"].Columns.Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "1", "x" }, Texts(result.Rows.Single()));
    }

    [TestMethod]
    public async Task Insert_EnUnaLineaYEnVariasLineas()
    {
        var result = await Read(Header + """
            CREATE TABLE `t` (`id` int(11) NOT NULL, `name` varchar(20), `amount` decimal(10,2)) ENGINE=InnoDB;
            LOCK TABLES `t` WRITE;
            /*!40000 ALTER TABLE `t` DISABLE KEYS */;
            INSERT INTO `t` VALUES (1,'one',1.50),(2,'two',NULL);
            INSERT INTO `t` VALUES
            (3,'three',3.00),
            (4,'four',4.25);
            /*!40000 ALTER TABLE `t` ENABLE KEYS */;
            UNLOCK TABLES;
            """);

        Assert.AreEqual(4, result.Rows.Count);
        CollectionAssert.AreEqual(new[] { "1", "one", "1.50" }, Texts(result.Rows[0]));
        CollectionAssert.AreEqual(new[] { "2", "two", null }, Texts(result.Rows[1]));
        CollectionAssert.AreEqual(new[] { "3", "three", "3.00" }, Texts(result.Rows[2]));
        CollectionAssert.AreEqual(new[] { "4", "four", "4.25" }, Texts(result.Rows[3]));
        Assert.IsTrue(result.Rows.All(r => ReferenceEquals(r.Table, result.Tables["t"]) && ReferenceEquals(r.Columns, r.Table.Columns)));
    }

    [TestMethod]
    public async Task Insert_ConListaDeColumnas()
    {
        var result = await Read("""
            CREATE TABLE `t` (`id` int(11) NOT NULL, `name` varchar(20), `amount` decimal(10,2));
            INSERT INTO `t` (`name`, `ID`) VALUES ('x',9),('y',10);
            INSERT INTO t(amount) VALUES(7.5);
            """);

        var first = result.Rows[0];
        CollectionAssert.AreEqual(new[] { "name", "id" }, first.Columns.Select(c => c.Name).ToArray());
        Assert.AreEqual("int(11)", first.Columns[1].Type);
        Assert.AreEqual("9", first["id"]);
        Assert.AreEqual("9", first["ID"]);
        Assert.AreEqual("x", first["name"]);
        Assert.ThrowsExactly<KeyNotFoundException>(() => first["amount"]);
        Assert.AreEqual("10", result.Rows[1]["id"]);
        Assert.AreEqual("7.5", result.Rows[2]["amount"]);
        Assert.AreEqual(1, result.Rows[2].Values.Count);
    }

    [TestMethod]
    public async Task Insert_Variantes()
    {
        var result = await Read("""
            CREATE TABLE `t` (`id` int);
            INSERT IGNORE INTO `t` VALUES (1);
            REPLACE INTO `t` VALUES (2);
            insert into `t` value (3);
            INSERT /*!40000 IGNORE */ INTO `t` VALUES (4);
            INSERT LOW_PRIORITY INTO `prueba`.`t` VALUES (5);
            INSERT `t` VALUES (6);
            INSERT INTO `t` VALUES
            (7),
            (8)
            """);

        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "5", "6", "7", "8" }, result.Rows.Select(r => (string)r.Values[0]!).ToArray());
    }

    [TestMethod]
    public async Task Cadenas_ConTodosLosEscapes()
    {
        var result = await Read("""
            CREATE TABLE `s` (`id` int NOT NULL, `v` text);
            INSERT INTO `s` VALUES
            (1,'a\0b'),
            (2,'it\'s'),
            (3,'say \"hi\"'),
            (4,'x\by'),
            (5,'l1\nl2'),
            (6,'a\rb'),
            (7,'a\tb'),
            (8,'a\Zb'),
            (9,'back\\slash'),
            (10,'100\%'),
            (11,'a\_b'),
            (12,'it''s'),
            (13,'a\zb'),
            (14,"d""q"),
            (15,"it's"),
            (16,''),
            (17,'end\\'),
            (18,'x\''),
            (19,'a;b -- c /* d */ e # f (g),(h)'),
            (20,'ñandú ✓ 😀'),
            (21,'\ñ'),
            (22,"say \"hi\""),
            (23,'''');
            """);

        string[] expected =
        [
            "a\0b", "it's", "say \"hi\"", "x\by", "l1\nl2", "a\rb", "a\tb", "a\u001ab",
            "back\\slash", "100\\%", "a\\_b", "it's", "azb", "d\"q", "it's", "", "end\\", "x'",
            "a;b -- c /* d */ e # f (g),(h)", "ñandú ✓ 😀", "ñ", "say \"hi\"", "'",
        ];
        CollectionAssert.AreEqual(expected, result.Rows.Select(r => (string)r["v"]!).ToArray());
    }

    [TestMethod]
    public async Task Cadenas_ConSaltosDeLineaSinEscapar()
    {
        var result = await Read("CREATE TABLE `s` (`v` text);\nINSERT INTO `s` VALUES ('a\nb\r\nc;\n-- d\n');\n");

        Assert.AreEqual("a\nb\r\nc;\n-- d\n", result.Rows.Single()["v"]);
    }

    [TestMethod]
    public async Task Nombres_ConComillasInvertidasDoblesEspaciosYAcentos()
    {
        var result = await Read("""
            CREATE TABLE `we``ird name` (`c``ol` int, `descripción` varchar(5), `b b` int);
            INSERT INTO `we``ird name` (`descripción`, `b b`) VALUES ('x', 2);
            """);

        var table = result.Tables["we`ird name"];
        CollectionAssert.AreEqual(new[] { "c`ol", "descripción", "b b" }, table.Columns.Select(c => c.Name).ToArray());
        Assert.AreEqual("x", result.Rows.Single()["descripción"]);
        Assert.AreEqual("2", result.Rows.Single()["b b"]);
    }

    [TestMethod]
    public async Task Valores_NullYNumerosComoTexto()
    {
        var result = await Read("""
            CREATE TABLE `n` (`a` int, `b` int, `c` int, `d` int, `e` int, `f` int, `g` decimal, `h` decimal, `i` decimal, `j` decimal, `k` double, `l` double, `m` double, `o` decimal(65,30), `p` bigint unsigned, `q` varchar(10));
            INSERT INTO `n` VALUES (NULL,null,Null,0,-1,+2,3.14,-0.50,.5,5.,1e10,1.5E-10,-2e+3,123456789012345678901234567890.123456789012345678901234567890,18446744073709551615,'123');
            """);

        CollectionAssert.AreEqual(
            new[]
            {
                null, null, null, "0", "-1", "+2", "3.14", "-0.50", ".5", "5.", "1e10", "1.5E-10", "-2e+3",
                "123456789012345678901234567890.123456789012345678901234567890", "18446744073709551615", "123",
            },
            Texts(result.Rows.Single()));
    }

    [TestMethod]
    public async Task Valores_Binarios()
    {
        var result = await Read("""
            CREATE TABLE `b` (`id` int, `v` longblob);
            INSERT INTO `b` VALUES
            (1,0xDEADBEEF),
            (2,0xdeadbeef),
            (3,0x1F1),
            (4,X'4142'),
            (5,x''),
            (6,_binary 'ab\0c\n\'\\'),
            (7,_binary 0x4142),
            (8,_binary ''),
            (9,_utf8mb4 'ñ'),
            (10,_utf8'x'),
            (11,b'101'),
            (12,b'1'),
            (13,B'100000001'),
            (14,b''),
            (15,_binary'ab');
            """);

        byte[]?[] expected =
        [
            [0xDE, 0xAD, 0xBE, 0xEF], [0xDE, 0xAD, 0xBE, 0xEF], [0x01, 0xF1], [0x41, 0x42], [],
            [(byte)'a', (byte)'b', 0, (byte)'c', (byte)'\n', (byte)'\'', (byte)'\\'], [0x41, 0x42], [],
            null, null, [0x05], [0x01], [0x01, 0x01], [], [(byte)'a', (byte)'b'],
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var value = result.Rows[i]["v"];
            if (expected[i] is { } bytes)
            {
                CollectionAssert.AreEqual(bytes, value as byte[], $"fila {i + 1}");
            }
        }

        Assert.AreEqual("ñ", result.Rows[8]["v"]);
        Assert.AreEqual("x", result.Rows[9]["v"]);
    }

    [TestMethod]
    public async Task Valores_BinarioConBytesQueNoSonUtf8()
    {
        byte[] sql =
        [
            .. "CREATE TABLE `b` (`v` varbinary(8)); INSERT INTO `b` VALUES (_binary '"u8,
            0xFF, 0xFE, 0x00, 0x80,
            .. "');"u8,
        ];
        var result = await Read(sql);

        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFE, 0x00, 0x80 }, result.Rows.Single()["v"] as byte[]);
    }

    [TestMethod]
    public async Task Comentarios_EnCualquierLugar()
    {
        var result = await Read("""
            -- comentario de línea con apóstrofe: don't; y punto y coma;
            # otro comentario, con 'comilla
            /* bloque con ' comilla; y ; punto y coma */
            CREATE TABLE `c` (`id` int, `v` varchar(10)); -- al final de la línea
            --
            -- Dumping data for table `c`
            --

            INSERT INTO `c` VALUES -- después de VALUES
            (1,'a'), /* entre filas */ (2,'b'),
            # comentario entre filas
            (3, /* dentro de la fila */ 'c') /* antes del cierre */
            -- antes del punto y coma
            ;
            /*!50001 SELECT 'a;b'; */;
            SET @a=1--1; INSERT INTO `c` VALUES (4,'d');
            /* final sin salto */
            """);

        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, result.Rows.Select(r => (string)r["v"]!).ToArray());
    }

    [TestMethod]
    public async Task Sentencias_QueNoSonDatoSeIgnoran()
    {
        var result = await Read("""
            SET NAMES utf8mb4;
            SET @saved = 'a;b';
            /*!40101 SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
            CREATE DATABASE /*!32312 IF NOT EXISTS*/ `db` /*!40100 DEFAULT CHARACTER SET utf8mb4 */;
            USE `db`;
            DROP TABLE IF EXISTS `t`;
            CREATE TABLE `t` (`id` int NOT NULL, `v` varchar(20)) ENGINE=InnoDB;
            DROP TABLE IF EXISTS `vista`;
            /*!50001 CREATE TABLE `vista` (`x` int) ENGINE=MyISAM */;
            /*!50001 DROP VIEW IF EXISTS `vista`*/;
            /*!50001 CREATE VIEW `vista` AS select `t`.`id` AS `x` from `t` where `t`.`v` = 'a;b' */;
            CREATE VIEW `v2` AS SELECT 1;
            LOCK TABLES `t` WRITE;
            INSERT INTO `t` VALUES (1,'uno');
            UNLOCK TABLES;
            START TRANSACTION;
            ALTER TABLE `t` ADD COLUMN `z` int;
            COMMIT;
            DELIMITER ;;
            /*!50003 CREATE*/ /*!50017 DEFINER=`root`@`localhost`*/ /*!50003 TRIGGER `trg` BEFORE INSERT ON `t` FOR EACH ROW BEGIN
              INSERT INTO `t` VALUES (99, 'desde el trigger');
              SET NEW.v = 'x;y';
            END */;;
            CREATE PROCEDURE `p`()
            BEGIN
              SET @a = 1;
              INSERT INTO `t` VALUES (98, 'desde el procedimiento');
              SELECT ';;' AS semis;
            END;;
            CREATE TRIGGER `trg2` BEFORE UPDATE ON `t` FOR EACH ROW BEGIN
              SET NEW.v = 'x';
              INSERT INTO `t` VALUES (97, 'desde el trigger sin comentarios');
            END;;
            DELIMITER ;
            INSERT INTO `t` VALUES (2,'dos');
            """);

        CollectionAssert.AreEqual(new[] { "t" }, result.Tables.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "1", "2" }, result.Rows.Select(r => (string)r["id"]!).ToArray());
    }

    [TestMethod]
    public async Task VariosVolcadosSeguidos_ComoLosJuntaElParalelo()
    {
        // Un script de volcado puede juntar en un archivo la salida de las tablas sin filtro y la de dos con --where.
        var result = await Read(Sample + "-- Dump completed on 2026-11-23 23:50:01\n" + Header + """
            CREATE TABLE `tblconfiguration` (`setting` varchar(64), `value` mediumtext);
            --
            -- WHERE:  setting IN ('AutoTermination')
            --
            INSERT INTO `tblconfiguration` VALUES ('AutoTermination','on');
            -- Dump completed on 2026-11-23 23:50:02
            """);

        CollectionAssert.AreEquivalent(new[] { "t", "tblconfiguration" }, result.Tables.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "t", "t", "tblconfiguration" }, result.Rows.Select(r => r.Table.Name).ToArray());
        Assert.AreEqual("on", result.Rows[^1]["value"]);
    }

    [TestMethod]
    public async Task Tablas_SinFilasTambienQuedanEnElEsquema()
    {
        var result = await Read("CREATE TABLE `a` (`id` int);\nCREATE TABLE `b` (`id` int, `x` text);\nINSERT INTO `b` VALUES (1,'y');\n");

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, result.Tables.Keys.ToArray());
        Assert.AreEqual(1, result.Rows.Count);
    }

    [TestMethod]
    public async Task Archivo_ConFinesDeLineaWindowsYBomUtf8()
    {
        var unix = Sample.ReplaceLineEndings("\n");
        var expected = Render(await Read(unix));

        var windows = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(unix.ReplaceLineEndings("\r\n"))).ToArray();

        Assert.AreEqual(expected, Render(await Read(windows)));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(7)]
    public async Task Stream_QueEntregaPocosBytesPorLecturaNoEsElFinDelArchivo(int chunk)
    {
        var sql = Encoding.UTF8.GetBytes(Sample);
        var expected = Render(await ReadWith(sql, 1 << 20));

        foreach (var bufferSize in new[] { 4, 64, 1 << 20 })
        {
            await using var dump = new DumpReader(new ChunkedStream(sql, chunk), bufferSize: bufferSize);
            var rows = new List<DumpRow>();
            await foreach (var row in dump.ReadRowsAsync())
            {
                rows.Add(row);
            }

            Assert.AreEqual(expected, Render(new Result(dump.Tables, rows)), $"búfer de {bufferSize} bytes");
        }
    }

    [TestMethod]
    public async Task Archivo_EnUtf16SeRechaza()
    {
        var sql = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("CREATE TABLE `t` (`id` int);")).ToArray();

        Assert.Contains("UTF-16", await Fails(sql));
    }

    [TestMethod]
    public async Task Fila_MasGrandeQueElBufer()
    {
        var big = new string('x', 300_000) + "\\n" + new string('y', 100_000);
        var hex = Convert.ToHexString(new byte[200_000]);
        var sql = $"CREATE TABLE `t` (`id` int, `v` longtext, `b` longblob);\nINSERT INTO `t` VALUES (1,'{big}',0x{hex}),(2,'z',NULL);\n";

        var result = await ReadWith(Encoding.UTF8.GetBytes(sql), 1024);

        Assert.AreEqual(2, result.Rows.Count);
        Assert.AreEqual(new string('x', 300_000) + "\n" + new string('y', 100_000), result.Rows[0]["v"]);
        Assert.AreEqual(200_000, ((byte[])result.Rows[0]["b"]!).Length);
        Assert.AreEqual("z", result.Rows[1]["v"]);
    }

    [TestMethod]
    public async Task Abrir_ReconocePorLosBytesUnArchivoSqlYUnoSqlGz()
    {
        const string sql = "CREATE TABLE `t` (`id` int, `v` varchar(9));\nINSERT INTO `t` VALUES (1,'a'),(2,'b');\n";
        var plain = Path.GetTempFileName();
        var zipped = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(plain, sql);
            await using (var gz = new GZipStream(File.Create(zipped), CompressionLevel.Optimal))
            {
                await gz.WriteAsync(Encoding.UTF8.GetBytes(sql));
            }

            foreach (var path in new[] { plain, zipped })
            {
                await using var dump = DumpReader.Open(path);
                var values = new List<string>();
                await foreach (var row in dump.ReadRowsAsync())
                {
                    values.Add((string)row["v"]!);
                }

                CollectionAssert.AreEqual(new[] { "a", "b" }, values, path);
            }
        }
        finally
        {
            File.Delete(plain);
            File.Delete(zipped);
        }
    }

    [TestMethod]
    public async Task Abrir_UnArchivoQueNoExisteEsIOException() =>
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var dump = DumpReader.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            await foreach (var row in dump.ReadRowsAsync())
            {
                Assert.IsNotNull(row);
            }
        });

    [TestMethod]
    public async Task ReadRowsAsync_SoloSePuedeRecorrerUnaVez()
    {
        await using var dump = new DumpReader(new MemoryStream("CREATE TABLE `t` (`id` int);"u8.ToArray()));
        await foreach (var row in dump.ReadRowsAsync())
        {
            Assert.IsNotNull(row);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in dump.ReadRowsAsync())
            {
                Assert.IsNotNull(row);
            }
        });
    }

    [TestMethod]
    public async Task Error_InsertSinCreateTable() =>
        Assert.Contains("nope", await Fails("INSERT INTO `nope` VALUES (1);"));

    [TestMethod]
    public async Task Error_ColumnaQueNoExiste() =>
        Assert.Contains("zzz", await Fails("CREATE TABLE `t` (`id` int); INSERT INTO `t` (`zzz`) VALUES (1);"));

    [TestMethod]
    [DataRow("INSERT INTO `t` VALUES (1);")]
    [DataRow("INSERT INTO `t` VALUES (1,2,3);")]
    [DataRow("INSERT INTO `t` (`a`) VALUES (1,2);")]
    public async Task Error_CantidadDeValoresDistintaDeLaDeColumnas(string insert) =>
        Assert.Contains("Tabla t", await Fails("CREATE TABLE `t` (`a` int, `b` int); " + insert));

    [TestMethod]
    [DataRow("INSERT INTO `t` VALUES (1,'abc")]
    [DataRow("INSERT INTO `t` VALUES (1,'abc\\")]
    [DataRow("INSERT INTO `t` VALUES (1,'a'),")]
    [DataRow("INSERT INTO `t` VALUES (1,'a')  ,")]
    [DataRow("INSERT INTO `t` VALUES (1,'a'")]
    [DataRow("INSERT INTO `t` VALUES")]
    [DataRow("INSERT INTO `t` VALUES (1,0xAB")]
    [DataRow("INSERT INTO `t` VALUES (1,12")]
    [DataRow("INSERT INTO `t` VALUES (1,'a') /* sin cerrar")]
    [DataRow("/* sin cerrar")]
    [DataRow("CREATE TABLE `u` (`id` int")]
    public async Task Error_ArchivoTruncado(string tail) =>
        await Fails("CREATE TABLE `t` (`a` int, `b` int);\n" + tail);

    [TestMethod]
    [DataRow("(1,CURRENT_TIMESTAMP)", "no soportado")]
    [DataRow("(1,DEFAULT)", "no soportado")]
    [DataRow("(1,1+1)", "se esperaba")]
    [DataRow("(1,12abc)", "ilegible")]
    [DataRow("(1,1e)", "ilegible")]
    [DataRow("(1,0xZZ)", "ilegible")]
    [DataRow("(1,x'4Z')", "ilegible")]
    [DataRow("(1,b'102')", "ilegible")]
    [DataRow("(1,_latin1 'x')", "Introductor")]
    [DataRow("(1,_binary 5)", "cadena")]
    [DataRow("(1 2)", "se esperaba")]
    public async Task Error_ValorQueElLectorNoSoporta(string row, string expected) =>
        Assert.Contains(expected, await Fails("CREATE TABLE `t` (`a` int, `b` int); INSERT INTO `t` VALUES " + row + ";"), StringComparison.OrdinalIgnoreCase);

    [TestMethod]
    public async Task Error_InsertQueNoEsValues() =>
        Assert.Contains("VALUES", await Fails("CREATE TABLE `t` (`a` int); INSERT INTO `t` SELECT 1;"));

    [TestMethod]
    public async Task Error_TextoQueNoEsUtf8()
    {
        byte[] sql = [.. "CREATE TABLE `t` (`v` text); INSERT INTO `t` VALUES ('ab"u8, 0xFF, .. "');"u8];

        Assert.Contains("UTF-8", await Fails(sql));
    }

    [TestMethod]
    public void Constructor_RechazaBufferInvalido() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DumpReader(new MemoryStream(), bufferSize: 0));
}
