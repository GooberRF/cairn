namespace Cairn.Vf.Model;

/// <summary>How serious a <see cref="VfProblem"/> is.</summary>
public enum VfSeverity { Error, Warning, Information }

/// <summary>One finding about a font: a code ("VF060"), a message written for the user, and the glyph it concerns, if any.</summary>
/// <param name="Severity">Error (the game fails or draws garbage), warning (likely a mistake) or information.</param>
/// <param name="Code">Stable code.</param>
/// <param name="Message">What is wrong and what the game does about it.</param>
/// <param name="Glyph">The glyph index it concerns, or null for the whole font.</param>
public sealed record VfProblem(VfSeverity Severity, string Code, string Message, int? Glyph = null)
{
    public override string ToString() => $"{Code} {Severity}: {Message}";
}
