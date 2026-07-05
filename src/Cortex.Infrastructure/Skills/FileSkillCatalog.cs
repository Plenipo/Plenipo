using System.Diagnostics;
using Cortex.Application.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Skills;

/// <summary>
/// Loads agent skills from a directory of <c>{skill-name}/SKILL.md</c> bundles (frontmatter
/// name/description + instruction body, optional resource and script files) — the same on-disk
/// format MAF's file-based skills and agentskills.io use. The catalog is read once at first use:
/// skills are deploy-time content, so a content change means a redeploy, not a rescan.
/// Resource and script paths are confined to the skill's own directory (traversal rejected).
/// </summary>
public sealed partial class FileSkillCatalog(
    IOptions<SkillsOptions> options,
    ILogger<FileSkillCatalog> logger) : ISkillCatalog
{
    private readonly Lazy<Dictionary<string, LoadedSkill>> _skills = new(() => Scan(options.Value, logger));

    private sealed record LoadedSkill(string Name, string Description, string Instructions, string Directory);

    public bool IsEnabled => options.Value.Enabled && _skills.Value.Count > 0;

    public IReadOnlyList<SkillSummary> List() =>
        _skills.Value.Values
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new SkillSummary(s.Name, s.Description))
            .ToList();

    public string? GetInstructions(string skillName) =>
        _skills.Value.TryGetValue(skillName.Trim(), out var skill) ? skill.Instructions : null;

    public string? ReadResource(string skillName, string resourcePath)
    {
        var file = ResolveWithinSkill(skillName, resourcePath);
        return file is not null && File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public async Task<string> RunScriptAsync(
        string skillName, string scriptPath, string? arguments, CancellationToken cancellationToken = default)
    {
        var file = ResolveWithinSkill(skillName, scriptPath);
        if (file is null || !File.Exists(file))
        {
            return $"No script '{scriptPath}' exists in skill '{skillName}'.";
        }

        var interpreter = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".py" => "python",
            ".js" => "node",
            ".sh" => "bash",
            ".ps1" => "pwsh",
            _ => null,
        };
        if (interpreter is null)
        {
            return $"Script type '{Path.GetExtension(file)}' is not supported (py, js, sh, ps1).";
        }

        var psi = new ProcessStartInfo
        {
            FileName = interpreter,
            WorkingDirectory = Path.GetDirectoryName(file)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(interpreter is "pwsh" ? "-File" : file);
        if (interpreter is "pwsh")
        {
            psi.ArgumentList.Add(file);
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            // The model passes one argument string; split on spaces outside quotes is overkill for
            // v1 — scripts receive it as a single argv entry and parse as they document.
            psi.ArgumentList.Add(arguments);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.ScriptTimeoutSeconds)));

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return $"Could not start '{interpreter}' — is it installed on the host?";
            }

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                cancellationToken.ThrowIfCancellationRequested();
                return $"Script '{scriptPath}' exceeded the {options.Value.ScriptTimeoutSeconds}s timeout and was killed.";
            }

            var output = (await stdout).Trim();
            var errors = (await stderr).Trim();
            return process.ExitCode == 0
                ? output.Length > 0 ? output : "(script completed with no output)"
                : $"Script failed with exit code {process.ExitCode}.\n{errors}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skill script {Skill}/{Script} failed to run", skillName, scriptPath);
            return $"Script could not run: {ex.Message}";
        }
    }

    /// <summary>Resolves a relative path inside the skill's directory; null when the skill is unknown or the path escapes it.</summary>
    private string? ResolveWithinSkill(string skillName, string relativePath)
    {
        if (!_skills.Value.TryGetValue(skillName.Trim(), out var skill))
        {
            return null;
        }

        var root = Path.GetFullPath(skill.Directory);
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath.Trim()));
        return resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? resolved
            : null; // traversal attempt — outside the skill bundle
    }

    private static Dictionary<string, LoadedSkill> Scan(SkillsOptions opts, ILogger logger)
    {
        var skills = new Dictionary<string, LoadedSkill>(StringComparer.Ordinal);
        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.Path))
        {
            return skills;
        }

        // Relative paths resolve against the app base first (published layout), then the working
        // directory (dev runs where content root and cwd diverge).
        var root = Path.IsPathRooted(opts.Path)
            ? opts.Path
            : new[] { Path.Combine(AppContext.BaseDirectory, opts.Path), Path.GetFullPath(opts.Path) }
                .FirstOrDefault(Directory.Exists) ?? opts.Path;
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Skills:Enabled is true but the skills directory '{Path}' does not exist", root);
            return skills;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifest = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(manifest))
            {
                continue;
            }

            var (name, description, body) = ParseSkillFile(File.ReadAllText(manifest));
            var dirName = Path.GetFileName(dir);
            if (name is null || description is null)
            {
                logger.LogWarning("Skill at {Dir} skipped: SKILL.md frontmatter needs name and description", dir);
                continue;
            }

            if (!string.Equals(name, dirName, StringComparison.Ordinal))
            {
                // The agentskills.io contract: frontmatter name must match the directory name.
                logger.LogWarning("Skill at {Dir} skipped: frontmatter name '{Name}' must match the directory name", dir, name);
                continue;
            }

            skills[name] = new LoadedSkill(name, description, body, dir);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Loaded {Count} agent skill(s) from {Path}", skills.Count, root);
        }

        return skills;
    }

    /// <summary>Minimal frontmatter parse: a leading <c>---</c> block with <c>name:</c> and <c>description:</c> lines.</summary>
    public static (string? Name, string? Description, string Body) ParseSkillFile(string content)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (null, null, text);
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (null, null, text);
        }

        string? name = null, description = null;
        foreach (var line in text[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key == "name")
            {
                name = value;
            }
            else if (key == "description")
            {
                description = value;
            }
        }

        var bodyStart = text.IndexOf('\n', end + 4);
        var body = bodyStart >= 0 ? text[(bodyStart + 1)..].Trim() : "";
        return (name, description, body);
    }
}
