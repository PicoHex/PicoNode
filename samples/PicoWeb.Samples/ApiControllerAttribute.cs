namespace PicoWeb.Samples;

/// <summary>
/// Enables convention-based controller discovery outside the Controllers/ folder.
/// The Controllers.Gen source generator checks for this attribute by name.
/// </summary>
public class ApiControllerAttribute : Attribute;
