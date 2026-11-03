namespace Pishkhan.Modules.Templates.Contracts;

/// <summary>
/// A fact every shop of an industry should tell the AI (BE-2.9; the knowledge score of BE-1.9 counts them). <see cref="Prompt"/> is the
/// Persian question the panel asks the owner, for example «هزینه ارسال به شهرستان‌ها چقدر است؟».
/// </summary>
public sealed record RequiredFact(string Key, string Label, string Prompt);

/// <summary>In-process queries other modules may use.</summary>
public interface ITemplateQueries
{
    /// <summary>The required facts of the industry's published template, or an empty list when it has none.</summary>
    Task<IReadOnlyList<RequiredFact>> GetRequiredFactsAsync(string industry, CancellationToken cancellationToken);
}
