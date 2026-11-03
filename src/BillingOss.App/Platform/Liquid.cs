using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Fluid;
using Fluid.Values;
using Microsoft.Extensions.FileProviders;

namespace BillingOss.App.Platform;

/// <summary>
/// El motor de plantillas tipo Liquid (D-13), uno solo para los avisos por correo y para el PDF de los comprobantes:
/// Fluid con los mismos topes de pasos, recursión y tamaño, sin acceso a archivos (include y render no encuentran
/// nada) y con la salida codificada como HTML.
/// </summary>
public static class Liquid
{
    public const int MaxSteps = 50_000;

    public const int MaxRecursion = 16;

    /// <summary>Tope de lo que una plantilla escribe o guarda en una variable. Un comprobante largo, con el QR embebido, entra holgado.</summary>
    public const int MaxOutput = 1024 * 1024;

    /// <summary>Codifica lo que puede romper el HTML y deja las letras con tilde tal cual.</summary>
    public static HtmlEncoder Html { get; } = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly FluidParser Parser = new();

    /// <summary>
    /// Opciones con los topes. Los modelos son planos: diccionarios y listas, o los tipos que se registran acá, de los
    /// que solo se leen sus miembros con nombres en snake_case.
    /// </summary>
    public static TemplateOptions Options(params Type[] model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var options = new TemplateOptions
        {
            MaxSteps = MaxSteps,
            MaxRecursion = MaxRecursion,
            FileProvider = new NullFileProvider(),
            Assigned = (_, value, _) => Limit(value),
            Captured = (_, value, _) => Limit(value),
        };
        options.MemberAccessStrategy.MemberNameStrategy = MemberNameStrategies.SnakeCase;
        foreach (var type in model)
        {
            options.MemberAccessStrategy.Register(type);
        }

        return options;
    }

    /// <summary>Devuelve el error de sintaxis, o null si la plantilla se puede usar.</summary>
    public static string? Validate(string source) => Parser.TryParse(source, out _, out var error) ? null : error;

    public static IFluidTemplate Parse(string source) =>
        Parser.TryParse(source, out var template, out var error) ? template : throw new InvalidOperationException($"La plantilla no compila: {error}");

    /// <summary>Escribe la plantilla y corta apenas la salida pasa el tope, antes de llenar la memoria.</summary>
    public static async Task<string> Render(IFluidTemplate template, TemplateContext context, TextEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(template);
        await using var writer = new LimitedWriter();
        await template.RenderAsync(writer, context, encoder, isolateContext: false).ConfigureAwait(false);
        return writer.ToString();
    }

    private static ValueTask<FluidValue> Limit(FluidValue value) =>
        value is StringValue s && s.ToStringValue().Length > MaxOutput
            ? throw new InvalidOperationException("La plantilla supera el tamaño máximo.")
            : ValueTask.FromResult(value);

    private sealed class LimitedWriter : StringWriter
    {
        public LimitedWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override void Write(char value)
        {
            base.Write(value);
            Check();
        }

        public override void Write(string? value)
        {
            base.Write(value);
            Check();
        }

        public override void Write(char[] buffer, int index, int count)
        {
            base.Write(buffer, index, count);
            Check();
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            base.Write(buffer);
            Check();
        }

        private void Check()
        {
            if (GetStringBuilder().Length > MaxOutput)
            {
                throw new InvalidOperationException("La plantilla supera el tamaño máximo.");
            }
        }
    }
}
