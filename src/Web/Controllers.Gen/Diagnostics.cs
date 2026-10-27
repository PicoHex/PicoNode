using System.Globalization;
using Microsoft.CodeAnalysis.Text;

namespace Controllers.Gen;

/// <summary>
/// The rules this generator reports, declared once (task T5.7, audit F-55).
/// <para>
/// Before this file there was no descriptor at all, and <c>PicoWeb.Gen</c> built one per
/// diagnostic it reported — a <see cref="DiagnosticDescriptor"/> is designed to be a static,
/// shared value, so both are fixed here: the descriptors below are <c>static readonly</c>, and
/// <see cref="All"/> is the single place that knows them.
/// </para>
/// <para>
/// The severity choices are deliberate, and each one is pinned by a test:
/// <list type="bullet">
/// <item><b>PCW001</b> (warning) — a discovered controller whose actions answer no HTTP verb.
/// The project still builds and the rest of the app still serves; the user has lost one
/// controller, and needs to be told before they ship a 404.</item>
/// <item><b>PCW002</b> (error) — a generic controller, which cannot be mapped at all. It is an
/// error because the alternative the generator used to produce was a hard build failure
/// (CS0246 with an unbound <c>T</c>): a controller the user wrote and the generator cannot map
/// is a build-time fact, so it must not be demoted to a warning that gets ignored.</item>
/// <item><b>PCW003</b> (warning) — a DTO that is serialised by a generated endpoint but carries no
/// <c>[PicoJsonSerializable]</c>. The generated code compiles and the endpoint mounts; it fails
/// per request, so failing the whole build would be disproportionate.</item>
/// </list>
/// </para>
/// </summary>
internal static class Diagnostics
{
    /// <summary><c>PCW001</c>: discovered, and no action matched an HTTP verb.</summary>
    internal const string ControllerMapsNoActionId = "PCW001";

    /// <summary><c>PCW002</c>: discovered, and cannot be mapped (generic controller).</summary>
    internal const string UnsupportedControllerId = "PCW002";

    /// <summary><c>PCW003</c>: a serialised DTO without <c>[PicoJsonSerializable]</c>.</summary>
    internal const string DtoNotSerializableId = "PCW003";

    private const string Category = "PicoNode.Web";

    internal static readonly DiagnosticDescriptor ControllerMapsNoAction = new(
        ControllerMapsNoActionId,
        title: "Controller maps no action",
        messageFormat: "Controller '{0}' matches no HTTP verb, so no endpoint was generated for it: add [HttpGet]/[HttpPost]/[HttpPut]/[HttpDelete]/[HttpPatch] to an action, or name the action Get*/Post*/Put*/Delete*/Patch*",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A controller is discovered by its Controllers/ folder (or by [ApiController]), and one endpoint is mapped per action that answers an HTTP verb. A controller whose actions all fail that test produces no endpoint, which is indistinguishable at run time from a route nobody ever registered."
    );

    internal static readonly DiagnosticDescriptor UnsupportedController = new(
        UnsupportedControllerId,
        title: "Controller cannot be mapped",
        messageFormat: "Controller '{0}' is generic, so its type parameters cannot be bound in an endpoint map and no endpoint was generated for it: make the controller non-generic, or move the generic part into a base class",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A generic controller has no type argument to bind, so every reference to it in the generated endpoint map would carry an unbound type parameter (CS0246). The controller is skipped rather than emitted broken, and this diagnostic says which controller and why."
    );

    internal static readonly DiagnosticDescriptor DtoNotSerializable = new(
        DtoNotSerializableId,
        title: "DTO is serialised without a generated serializer",
        messageFormat: "Type '{0}' is serialised as the JSON response of a generated controller endpoint but is not marked [PicoJsonSerializable], so no serializer is generated for it and the endpoint fails per request (under NativeAOT it cannot fall back): add [PicoJsonSerializable] to the type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A controller action that returns a DTO is emitted as PicoJetson.JsonSerializer.SerializeToUtf8Bytes(<dto>). PicoJetson.Gen only generates a serializer for a type marked [PicoJsonSerializable], so an unmarked DTO compiles and then throws at request time."
    );

    /// <summary>
    /// Every rule this generator can report. A static array, not a per-diagnostic construction:
    /// the lookup below is the only way a descriptor is reached, so an id that is spelled in a
    /// model but missing here cannot silently produce a diagnostic with no rule behind it.
    /// </summary>
    internal static readonly DiagnosticDescriptor[] All =
    [
        ControllerMapsNoAction,
        UnsupportedController,
        DtoNotSerializable,
    ];

    /// <summary>
    /// The descriptor behind an id, or <c>null</c> when the id is one this generator does not
    /// own. Returning <c>null</c> rather than throwing keeps a stale model from taking the whole
    /// generation down — the caller skips it.
    /// </summary>
    internal static DiagnosticDescriptor? DescriptorFor(string id)
    {
        foreach (var descriptor in All)
        {
            if (string.Equals(descriptor.Id, id, StringComparison.Ordinal))
                return descriptor;
        }

        return null;
    }
}

/// <summary>
/// One reported diagnostic, as values — the only shape a pipeline that caches by equality can
/// carry (task T5.6's constraint, which T5.7 had to respect while adding diagnostics).
/// <para>
/// A <see cref="Location"/> cannot travel down the pipeline: it is not a comparable value, and a
/// model holding one would be "different" on every run, so the whole source output — and every
/// emitted string — would be rebuilt on every keystroke. The path, the span and the line span
/// are the location, split into parts that compare by value; <see cref="ToLocation"/> puts them
/// back together in the source output, which is the only place a
/// <see cref="SourceProductionContext"/> exists to report through.
/// </para>
/// <para>
/// <see cref="Name"/> is the single message argument every rule in this generator takes: the
/// controller or the DTO the diagnostic is about.
/// </para>
/// </summary>
internal sealed record DiagnosticModel
{
    /// <summary>The descriptor id (<c>PCW001</c>…<c>PCW003</c>) — see <see cref="Diagnostics"/>.</summary>
    public string DescriptorId { get; }

    /// <summary>The file the diagnostic points into, exactly as the user's tree names it.</summary>
    public string FilePath { get; }

    /// <summary>The span inside <see cref="FilePath"/>, so the IDE can select the identifier.</summary>
    public TextSpan Span { get; }

    /// <summary>The same span in line/character terms, which is what <see cref="Location"/> needs.</summary>
    public LinePositionSpan LineSpan { get; }

    /// <summary>The message argument — the type this diagnostic is about.</summary>
    public string Name { get; }

    public DiagnosticModel(
        string descriptorId,
        string filePath,
        TextSpan span,
        LinePositionSpan lineSpan,
        string name
    )
    {
        DescriptorId = descriptorId;
        FilePath = filePath;
        Span = span;
        LineSpan = lineSpan;
        Name = name;
    }

    /// <summary>
    /// Describes <paramref name="location"/> for <paramref name="descriptor"/>, or <c>null</c>
    /// when there is no source location to point at. Null rather than a location-less diagnostic
    /// on purpose: <c>location: null</c> is exactly the defect this task exists to remove — a
    /// diagnostic nobody can navigate to.
    /// </summary>
    internal static DiagnosticModel? Create(
        DiagnosticDescriptor descriptor,
        Location? location,
        string name
    )
    {
        if (location is null || !location.IsInSource || location.SourceTree is null)
            return null;

        var lineSpan = location.GetLineSpan();
        return new DiagnosticModel(
            descriptor.Id,
            lineSpan.Path,
            location.SourceSpan,
            lineSpan.Span,
            name
        );
    }

    /// <summary>Rebuilds the navigable location in the source output.</summary>
    internal Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    /// <summary>
    /// Identity of the site, so one problem is reported once even when several controllers reach
    /// the same DTO. Value-derived, like every other field here.
    /// </summary>
    internal string Key =>
        string.Join(
            "|",
            DescriptorId,
            FilePath,
            Span.Start.ToString(CultureInfo.InvariantCulture),
            Span.Length.ToString(CultureInfo.InvariantCulture)
        );
}
