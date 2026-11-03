using System.Text;

namespace BillingOss.Importer.Tests;

[TestClass]
public sealed class InspectCommandTests
{
    [TestMethod]
    public async Task Inspect_ListaPorTablaLasFilasYLasColumnas()
    {
        const string sql = """
            CREATE TABLE `b` (`x` int(11) NOT NULL, KEY `x` (`x`));
            CREATE TABLE `a` (
              `id` int(10) unsigned NOT NULL AUTO_INCREMENT,
              `name` varchar(20) DEFAULT NULL,
              PRIMARY KEY (`id`)
            );
            CREATE TABLE `c` (`k` varchar(5));
            INSERT INTO `a` VALUES (1,'x'),(2,NULL);
            INSERT INTO `c` VALUES ('solo');
            """;
        await using var dump = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(sql)));
        using var output = new StringWriter { NewLine = "\n" };

        await InspectCommand.RunAsync(dump, output);

        Assert.AreEqual(
            "a: 2 filas\n  id    int(10) unsigned\n  name  varchar(20)\n"
            + "b: 0 filas\n  x  int(11)\n"
            + "c: 1 fila\n  k  varchar(5)\n"
            + "3 tablas, 3 filas\n",
            output.ToString());
    }

    [TestMethod]
    public async Task Inspect_UnaTablaConUnaFilaVaEnSingular()
    {
        await using var dump = new DumpReader(new MemoryStream("CREATE TABLE `a` (`id` int); INSERT INTO `a` VALUES (1);"u8.ToArray()));
        using var output = new StringWriter { NewLine = "\n" };

        await InspectCommand.RunAsync(dump, output);

        Assert.AreEqual("a: 1 fila\n  id  int\n1 tabla, 1 fila\n", output.ToString());
    }

    [TestMethod]
    public async Task Inspect_UnDumpVacioNoTieneTablas()
    {
        await using var dump = new DumpReader(new MemoryStream());
        using var output = new StringWriter { NewLine = "\n" };

        await InspectCommand.RunAsync(dump, output);

        Assert.AreEqual("0 tablas, 0 filas\n", output.ToString());
    }
}
