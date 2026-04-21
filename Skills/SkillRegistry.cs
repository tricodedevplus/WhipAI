namespace WhipAI.Skills;

/// <summary>
/// DI-registered lookup for skills. Built from the list of <see cref="ISkill"/>s
/// registered in <c>Program.cs</c>. Provides two operations: get by name
/// (controller uses this to dispatch invocations) and list all (used by
/// the catalog endpoint).
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, ISkill> _skills;

    public SkillRegistry(IEnumerable<ISkill> skills)
    {
        _skills = skills.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string name, out ISkill skill) => _skills.TryGetValue(name, out skill!);

    public IEnumerable<ISkill> All() => _skills.Values;
}
