using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Services;

internal sealed class AgentTemplates(AgentDbContext db, IClock clock) : IAgentTemplates
{
    public async Task<bool> ApplyProfileAsync(Guid workspaceId, TemplateAgentProfile template, CancellationToken cancellationToken)
    {
        if (await db.Profiles.AnyAsync(cancellationToken))
        {
            return false;
        }

        if (!AgentTones.All.Contains(template.Tone))
        {
            throw new InvalidOperationException($"Template tone '{template.Tone}' is unknown");
        }

        var now = clock.UtcNow;
        var profile = AgentProfile.Default(workspaceId);
        profile.Name = Clip(template.Name, AgentProfile.MaxName);
        profile.Tone = template.Tone;
        profile.BrandVoice = template.BrandVoice is { Length: > 0 } voice ? Clip(voice, AgentProfile.MaxBrandVoice) : null;
        profile.NeverSay = [.. template.NeverSay.Select(p => Clip(p, AgentProfile.MaxPhrase)).Take(AgentProfile.MaxList)];
        profile.Handoff.Keywords = [.. template.HandoffKeywords.Select(p => Clip(p, AgentProfile.MaxPhrase)).Take(AgentProfile.MaxList)];
        profile.CreatedAt = now;
        profile.UpdatedAt = now;
        db.Profiles.Add(profile);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The owner saved a profile at the same moment: theirs stands.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static string Clip(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
