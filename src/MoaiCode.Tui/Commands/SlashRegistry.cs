namespace MoaiCode.Tui.Commands;

/// <summary>슬래시 명령 레지스트리. 이름/별칭으로 디스패치.</summary>
public sealed class SlashRegistry
{
    private readonly Dictionary<string, ISlashCommand> _byName;

    public SlashRegistry(IEnumerable<ISlashCommand> commands)
    {
        _byName = new Dictionary<string, ISlashCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commands)
        {
            _byName[c.Name] = c;
        }

        // 별칭
        if (_byName.TryGetValue("exit", out var exit))
        {
            _byName["quit"] = exit;
        }
    }

    public IReadOnlyCollection<ISlashCommand> Commands => _byName.Values;

    public bool TryGet(string name, out ISlashCommand command)
        => _byName.TryGetValue(name, out command!);

    /// <summary>기본 빌트인 명령 셋.</summary>
    public static SlashRegistry CreateDefault()
    {
        var commands = new List<ISlashCommand>
        {
            new ToolsCommand(),
            new ModelCommand(),
            new SkillsCommand(),
            new McpCommand(),
            new CostCommand(),
            new UsageCommand(),
            new PermissionsCommand(),
            new PlanModeCommand(),
            new ActModeCommand(),
            new CheckpointCreateCommand(),
            new CheckpointsCommand(),
            new CheckpointDiffCommand(),
            new RestoreCommand(),
            new HistoryCommand(),
            new SessionsCommand(),
            new SaveCommand(),
            new ResumeCommand(),
            new InitCommand(),
            new ReviewCommand(),
            new SecurityReviewCommand(),
            new BugHunterCommand(),
            new SimplifyCommand(),
            new ClearCommand(),
            new ExitCommand(),
        };
        commands.Add(new HelpCommand(commands.ToList()));
        return new SlashRegistry(commands);
    }
}
