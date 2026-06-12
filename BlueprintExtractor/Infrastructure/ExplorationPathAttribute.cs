namespace BlueprintExtractor.Infrastructure;

/**
 * Assembly-level attribute that stores the .exploration/ directory path, injected at build time.
 * This lets the mod write discovery output directly into the project without hardcoding paths.
 */
[AttributeUsage(AttributeTargets.Assembly)]
public class ExplorationPathAttribute(string path) : Attribute {
  public string Path { get; } = path;
}