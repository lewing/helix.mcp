namespace HelixTool.Core;

/// <summary>A single match in a console log.</summary>
public record LogMatch(int LineNumber, string Line, List<string>? Context = null);
