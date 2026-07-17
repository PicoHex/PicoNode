namespace PicoNode.Agent.Domain;

public sealed class SkillInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool DisableModelInvocation { get; set; }
}
