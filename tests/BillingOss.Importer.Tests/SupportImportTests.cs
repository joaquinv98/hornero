using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.Importer.Tests;

/// <summary>Importación del soporte desde un origen sintético (sin datos de clientes reales).</summary>
[TestClass]
public sealed class SupportImportTests
{
    private const string Dump = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `country` text, `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` text, `language` text);
        CREATE TABLE `tblticketdepartments` (`id` int(11), `name` text, `description` text, `email` text, `clientsonly` text, `piperepliesonly` text, `noautoresponder` text, `hidden` text, `order` int(11), `host` text, `port` text, `login` text, `password` text, `feedback_request` tinyint(1));
        CREATE TABLE `tblticketstatuses` (`id` int(11), `title` varchar(64), `color` text, `sortorder` int(11), `showactive` int(11), `showawaiting` int(11), `autoclose` int(11));
        CREATE TABLE `tbltickets` (`id` int(11), `tid` varchar(128), `did` int(11), `userid` int(11), `name` text, `email` text, `cc` text, `date` datetime, `title` text, `message` text, `status` varchar(64), `urgency` text, `admin` text, `attachment` text, `lastreply` datetime, `flag` int(11), `service` text, `merged_ticket_id` int(11));
        CREATE TABLE `tblticketreplies` (`id` int(11), `tid` int(11), `userid` int(11), `name` text, `email` text, `date` datetime, `message` text, `admin` text, `attachment` text, `rating` int(11));
        CREATE TABLE `tblticketnotes` (`id` int(11), `ticketid` int(11), `admin` text, `date` datetime, `message` text, `attachments` text);
        CREATE TABLE `tblticketpredefinedcats` (`id` int(11), `parentid` int(11), `name` text);
        CREATE TABLE `tblticketpredefinedreplies` (`id` int(11), `catid` int(11), `name` text, `reply` text);
        CREATE TABLE `tblticketspamfilters` (`id` int(11), `type` text, `content` text);
        CREATE TABLE `tblticketescalations` (`id` int(11), `name` text, `departments` text, `statuses` text, `priorities` text, `timeelapsed` int(11), `newdepartment` text, `newpriority` text, `newstatus` text, `flagto` text, `notify` text, `addreply` text);
        CREATE TABLE `tblticketfeedback` (`id` int(11), `ticketid` int(11), `adminid` int(11), `rating` int(11), `comments` text, `datetime` datetime, `ip` text);
        INSERT INTO `tblcurrencies` VALUES (3,'ARS',1);
        INSERT INTO `tblclients` VALUES (501,'Ana','Prueba','','ana@example.com','AR',3,0.00,'2025-03-31','Active','spanish');
        INSERT INTO `tblticketdepartments` VALUES (1,'Soporte','Técnico','soporte@hosting.example','','','','',1,'mail.hosting.example','993','soporte@hosting.example','','1'),(5,'Abuso','','abuso@hosting.example','on','on','on','on',2,'','','','','0');
        INSERT INTO `tblticketstatuses` VALUES (1,'Open','',1,1,1,0),(2,'Answered','',2,1,0,1),(3,'Customer-Reply','',3,1,1,1),(4,'Closed','',4,0,0,0),(5,'Esperando proveedor','',5,1,0,0),(6,'Resuelto','',6,0,0,0);
        INSERT INTO `tbltickets` VALUES (100,'123456',1,501,'','','socio@example.com,no es correo','2025-05-01 10:00:00','No anda el VPS','Se cayó.','Answered','High','','1234_captura.png|log.txt','2025-05-02 12:00:00',0,'S999',0),(101,'654321',1,0,'Visitante','visita@example.org','','2025-05-03 09:00:00','Consulta','¿Venden dominios?','Esperando proveedor','Medium','','','2025-05-03 09:00:00',0,'',0),(102,'222222',1,501,'','','','2025-05-04 09:00:00','Duplicado','Otra vez lo mismo.','Customer-Reply','Low','','','2025-05-04 09:30:00',0,'D45',100),(103,'333333',9,501,'','','','2025-05-05 09:00:00','Departamento borrado','Aviso del staff.','Resuelto','Medium','Staff Uno','','2025-05-05 09:00:00',0,'',0);
        INSERT INTO `tblticketreplies` VALUES (1,100,0,'','','2025-05-02 12:00:00','Lo reiniciamos.','Staff Uno','',5),(2,102,501,'Ana Prueba','ana@example.com','2025-05-04 09:30:00','Sigue igual.','','',0),(3,999,0,'','','2025-05-04 09:30:00','Huérfana','','',0);
        INSERT INTO `tblticketnotes` VALUES (1,100,'Staff Uno','2025-05-02 12:05:00','Debe dos meses.','');
        INSERT INTO `tblticketpredefinedcats` VALUES (1,0,'General');
        INSERT INTO `tblticketpredefinedreplies` VALUES (1,1,'Saludo','Hola, gracias por escribir.'),(2,7,'Suelta','Texto.');
        INSERT INTO `tblticketspamfilters` VALUES (1,'sender','spam@example.net'),(2,'subject','ganaste'),(3,'phrase','');
        INSERT INTO `tblticketescalations` VALUES (1,'Sin respuesta','1','Open,Customer-Reply','Low,Medium',480,'5','High','','3','all','Seguimos con tu caso.');
        INSERT INTO `tblticketfeedback` VALUES (1,100,2,9,'Muy bien','2025-05-06 10:00:00','10.20.30.1'),(2,100,2,7,'Revisado','2025-05-07 10:00:00','10.20.30.1'),(3,999,2,3,'','2025-05-07 10:00:00','10.20.30.1');
        """;

    [TestMethod]
    public Task Soporte_ConIdsNumerosEstadosUnionesYNotas_SinContraseñasNiArchivos() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var support = await db.Set<Department>().SingleAsync(d => d.Id == 1);
        Assert.AreEqual(("Soporte", "soporte@hosting.example", true, true, false), (support.Name, support.Email, support.AutoReply, support.RequestFeedback, support.MailEnabled));
        Assert.AreEqual(("mail.hosting.example", 993, (byte[]?)null), (support.ImapHost, support.ImapPort, support.ImapPassword), "La casilla queda apagada y sin contraseña.");
        var abuse = await db.Set<Department>().SingleAsync(d => d.Id == 5);
        Assert.AreEqual((true, true, false), (abuse.Hidden, abuse.ClientsOnly, abuse.AutoReply));
        Assert.IsTrue((await db.Set<Department>().SingleAsync(d => d.Id == 9)).Hidden, "El departamento que el origen borró queda oculto de reemplazo.");

        var tickets = await db.Set<Ticket>().OrderBy(t => t.Id).ToDictionaryAsync(t => t.Id);
        var vps = tickets[100];
        Assert.AreEqual(("123456", (long?)501, "ana@example.com", "Ana Prueba"), (vps.Number, vps.AccountId, vps.RequesterEmail, vps.RequesterName));
        Assert.AreEqual((TicketStatus.AwaitingCustomer, TicketPriority.High, (long?)null), (vps.Status, vps.Priority, vps.ServiceId));
        CollectionAssert.AreEqual(new[] { "socio@example.com" }, vps.Cc);
        Assert.IsTrue(vps.Imported);
        Assert.AreEqual(20, vps.ReplyToken.Length);
        var guest = tickets[101];
        Assert.AreEqual(((long?)null, "visita@example.org", TicketStatus.InProgress, TicketPriority.Normal), (guest.AccountId, guest.RequesterEmail, guest.Status, guest.Priority));
        Assert.AreEqual((TicketStatus.Closed, (long?)100), (tickets[102].Status, tickets[102].MergedIntoId));
        Assert.AreEqual((TicketStatus.Closed, 9L), (tickets[103].Status, tickets[103].DepartmentId));

        var thread = await db.Set<TicketMessage>().Where(m => m.TicketId == 100).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { "Se cayó.", "Lo reiniciamos.", "Debe dos meses.", "Otra vez lo mismo.", "Sigue igual.", TicketService.MergedText("222222", "Duplicado") },
            thread.Select(m => m.Body).ToArray());
        Assert.AreEqual((MessageAuthor.Staff, "Staff Uno", false), (thread[1].Author, thread[1].AuthorName, thread[1].Internal));
        Assert.IsTrue(thread[2].Internal);
        Assert.IsTrue(thread.All(m => m.Source == MessageSource.Import));
        Assert.IsEmpty(await db.Set<TicketMessage>().Where(m => m.TicketId == 102).ToListAsync(), "Los mensajes del unido están en el que lo recibió.");
        Assert.AreEqual(MessageAuthor.Staff, (await db.Set<TicketMessage>().SingleAsync(m => m.TicketId == 103)).Author, "Lo abrió el staff.");
        var files = await db.Set<TicketAttachment>().OrderBy(a => a.FileName).ToListAsync();
        CollectionAssert.AreEqual(new[] { "captura.png", "log.txt" }, files.Select(f => f.FileName).ToArray());
        Assert.IsTrue(files.All(f => f.Missing && f.MessageId == thread[0].Id));
        Assert.IsFalse(await db.Set<AttachmentContent>().AnyAsync());

        var replies = await db.Set<SavedReply>().OrderBy(r => r.Name).ToListAsync();
        Assert.AreEqual(("Saludo", "General"), (replies[0].Name, replies[0].Category));
        Assert.IsNull(replies[1].Category);
        Assert.AreEqual(2, await db.Set<MailFilter>().CountAsync());
        var rule = await db.Set<EscalationRule>().SingleAsync();
        Assert.AreEqual((480, (long?)5, (TicketPriority?)TicketPriority.High, (TicketStatus?)null, true, "Seguimos con tu caso."), (rule.AfterMinutes, rule.NewDepartmentId, rule.NewPriority, rule.NewStatus, rule.Notify, rule.Reply));
        CollectionAssert.AreEqual(new[] { 1L }, rule.DepartmentIds);
        CollectionAssert.AreEqual(new[] { TicketStatus.Open }, rule.Statuses);
        CollectionAssert.AreEqual(new[] { TicketPriority.Low, TicketPriority.Normal }, rule.Priorities);
        var feedback = await db.Set<TicketFeedback>().SingleAsync();
        Assert.AreEqual((100L, 7, "Revisado", true), (feedback.TicketId, feedback.Rating, feedback.Comment, feedback.Imported), "Una por ticket: la última.");

        foreach (var (table, rows) in new[] { ("tblticketdepartments", 2), ("tbltickets", 4), ("tblticketreplies", 3), ("tblticketnotes", 1), ("tblticketpredefinedreplies", 2), ("tblticketspamfilters", 3), ("tblticketescalations", 1), ("tblticketfeedback", 3) })
        {
            Assert.AreEqual(rows, import.Figures[("support_rows", table)].Count, table);
        }

        Assert.AreEqual(1, import.Figures[("support_control", "respuestas_sin_ticket")].Count);
        Assert.AreEqual(1, import.Figures[("support_control", "encuestas_sin_ticket")].Count);
        Assert.AreEqual(1, import.Figures[("support_control", "tickets_unidos")].Count);
        Assert.AreEqual(1, import.Figures[("support_control", "tickets_de_dominio")].Count);
        var incidents = string.Join(" | ", import.Incidents);
        StringAssert.Contains(incidents, "Departamento 1: leía una casilla de correo");
        StringAssert.Contains(incidents, "Departamento 5: por correo solo aceptaba respuestas");
        StringAssert.Contains(incidents, "Departamento 9: lo usan tickets");
        StringAssert.Contains(incidents, "Regla de escalamiento 1: asignaba a una persona del staff");
        StringAssert.Contains(incidents, "Regla de escalamiento 1: le responde al cliente con su texto");

        // Las secuencias arrancan arriba de lo importado (D-17).
        await db.Database.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT nextval(pg_get_serial_sequence('support.tickets', 'id')), nextval(pg_get_serial_sequence('support.departments', 'id'))", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.AreEqual((104L, 10L), (reader.GetInt64(0), reader.GetInt64(1)));
    });

    [TestMethod]
    public async Task Soporte_ConLaCarpetaDeAdjuntos_TraeLosArchivosQueEstan_YNoLeeFueraDeElla()
    {
        var root = Directory.CreateTempSubdirectory("billing-oss-adjuntos-");
        try
        {
            var folder = root.CreateSubdirectory("attachments");
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "1234_captura.png"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "afuera.txt"), [9]);
            var source = Dump.Replace("'1234_captura.png|log.txt'", "'1234_captura.png|log.txt|../afuera.txt'", StringComparison.Ordinal);

            await SourceImportTests.WithImport(source, async (db, import) =>
            {
                var files = await db.Set<TicketAttachment>().OrderBy(a => a.FileName).ToDictionaryAsync(a => a.FileName);
                Assert.AreEqual((false, 3L), (files["captura.png"].Missing, files["captura.png"].Size));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await db.Set<AttachmentContent>().Where(c => c.AttachmentId == files["captura.png"].Id).Select(c => c.Content).SingleAsync());
                Assert.IsTrue(files["log.txt"].Missing, "El que no está en la carpeta entra sin archivo.");
                Assert.IsTrue(files["afuera.txt"].Missing, "Un nombre con carpetas no se busca: nunca lee fuera de la carpeta.");
                Assert.AreEqual(1, await db.Set<AttachmentContent>().CountAsync());
                Assert.AreEqual((1, 2), (import.Figures[("support_control", "adjuntos_con_archivo")].Count, import.Figures[("support_control", "adjuntos_sin_archivo")].Count));
            }, supportFiles: folder.FullName);
        }
        finally
        {
            root.Delete(true);
        }
    }

    /// <summary>El mismo origen, con el staff del sistema anterior y el ticket 100 asignado a la persona 2.</summary>
    private static readonly string WithStaff = Dump.Replace("'2025-05-02 12:00:00',0,'S999',0)", "'2025-05-02 12:00:00',2,'S999',0)", StringComparison.Ordinal) + """

        CREATE TABLE `tbladmins` (`id` int(11), `firstname` text, `lastname` text, `email` text, `password` text);
        INSERT INTO `tbladmins` VALUES (2,'Staff','Uno','Staff1@Hosting.example','hash'),(3,'Staff','Tres','staff3@hosting.example','hash');
        """;

    [TestMethod]
    public Task Soporte_ElStaffConUsuarioPorSuCorreo_ConservaAsignacionFirmaEncuestasYReglas() => SourceImportTests.WithImport(WithStaff, async (db, import) =>
    {
        var staff = await db.Set<StaffUser>().SingleAsync();
        Assert.AreEqual((long?)staff.Id, (await db.Set<Ticket>().SingleAsync(t => t.Id == 100)).AssignedStaffId);
        var signed = await db.Set<TicketMessage>().Where(m => m.TicketId == 100 && m.Author == MessageAuthor.Staff).ToListAsync();
        Assert.IsTrue(signed.All(m => m.StaffId == staff.Id), "La respuesta y la nota de Staff Uno quedan a su nombre.");
        Assert.AreEqual((long?)staff.Id, (await db.Set<TicketFeedback>().SingleAsync()).StaffId);
        Assert.IsNull((await db.Set<EscalationRule>().SingleAsync()).AssignStaffId, "La persona 3 no tiene usuario.");
        Assert.AreEqual((1, 1), (import.Figures[("support_control", "staff_con_usuario")].Count, import.Figures[("support_control", "staff_sin_usuario")].Count));
        StringAssert.Contains(string.Join(" | ", import.Incidents), "Regla de escalamiento 1: asignaba a una persona del staff que no tiene usuario");
    }, prepare: StaffUno);

    [TestMethod]
    public Task Soporte_DosPersonasConElMismoNombre_LaFirmaNoAlcanzaParaAtribuirMensajes() => SourceImportTests.WithImport(
        WithStaff.Replace("(3,'Staff','Tres','staff3@hosting.example','hash');", "(3,'Staff','Tres','staff3@hosting.example','hash'),(4,'Staff','Uno','otro@hosting.example','hash');", StringComparison.Ordinal),
        async (db, _) =>
        {
            var staff = await db.Set<StaffUser>().SingleAsync();
            Assert.AreEqual((long?)staff.Id, (await db.Set<Ticket>().SingleAsync(t => t.Id == 100)).AssignedStaffId, "Por su ID sí se sabe quién es.");
            Assert.IsTrue(await db.Set<TicketMessage>().Where(m => m.Author == MessageAuthor.Staff).AllAsync(m => m.StaffId == null), "Por el nombre repetido no.");
        }, prepare: StaffUno);

    /// <summary>El usuario de la persona 2 del origen, cargado antes de importar.</summary>
    private static async Task StaffUno(BillingDbContext db)
    {
        db.Add(new StaffUser { Email = "staff1@hosting.example", NormalizedEmail = "STAFF1@HOSTING.EXAMPLE", Name = "Staff Uno", Permissions = [StaffPermissions.SupportTickets], CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
}
