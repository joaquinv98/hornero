using BillingOss.Web.Content;

namespace BillingOss.App.Tests;

/// <summary>A dónde va cada dirección del sistema anterior que el proveedor ya difundió (sección 12 de diseno/contenidos.md).</summary>
[TestClass]
public sealed class LegacyUrlsTests
{
    [TestMethod]
    [DataRow(null, "/")]
    [DataRow("", "/")]
    [DataRow("/knowledgebase", "/conocimiento")]
    [DataRow("/knowledgebase/12/", "/conocimiento/12")]
    [DataRow("/knowledgebase/12", "/conocimiento/12")]
    [DataRow("/Knowledgebase/12/", "/conocimiento/12")]
    [DataRow("/knowledgebase/7/Servidores-de-juegos", "/conocimiento/c/7")]
    [DataRow("/knowledgebase/tag/imap", "/conocimiento?q=imap")]
    [DataRow("/knowledgebase/tag/Guía de juegos", "/conocimiento?q=Gu%C3%ADa%20de%20juegos")]
    [DataRow("/knowledgebase/tag", "/conocimiento")]
    [DataRow("/knowledgebase/search", "/conocimiento")]
    [DataRow("/knowledgebase/abc", "/conocimiento")]
    [DataRow("/knowledgebase/0/", "/conocimiento")]
    [DataRow("/knowledgebase/-5/", "/conocimiento")]
    [DataRow("/announcements", "/anuncios")]
    [DataRow("/announcements/5/Corte-programado", "/anuncios/5")]
    [DataRow("/announcements/x", "/anuncios")]
    [DataRow("/download", "/descargas")]
    [DataRow("/download/category/3/Juegos", "/descargas")]
    [DataRow("/store/vps", null)]
    [DataRow("/password/reset", null)]
    public void Target_LlevaCadaRutaVieja_ASuDireccionNueva(string? rp, string? expected) => Assert.AreEqual(expected, LegacyUrls.Target(rp));
}
